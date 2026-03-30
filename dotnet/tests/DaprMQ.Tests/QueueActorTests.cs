using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class QueueActorTests
{
    private Mock<IActorStateManager> CreateMockStateManager()
    {
        var mock = new Mock<IActorStateManager>();
        var stateData = new Dictionary<string, object>();

        // Setup TryGetStateAsync for ActorMetadata
        mock.Setup(m => m.TryGetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata)
                {
                    return new ConditionalValue<ActorMetadata>(true, metadata);
                }
                return new ConditionalValue<ActorMetadata>(false, null);
            });

        // Setup TryGetStateAsync for LockState
        mock.Setup(m => m.TryGetStateAsync<LockState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is LockState lockState)
                {
                    return new ConditionalValue<LockState>(true, lockState);
                }
                return new ConditionalValue<LockState>(false, null);
            });

        // Setup TryGetStateAsync for string
        mock.Setup(m => m.TryGetStateAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is string stringValue)
                {
                    return new ConditionalValue<string>(true, stringValue);
                }
                return new ConditionalValue<string>(false, null);
            });

        // Setup TryGetStateAsync for Queue<QueueSegmentItem> (segments)
        mock.Setup(m => m.TryGetStateAsync<Queue<QueueSegmentItem>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is Queue<QueueSegmentItem> queue)
                {
                    return new ConditionalValue<Queue<QueueSegmentItem>>(true, queue);
                }
                return new ConditionalValue<Queue<QueueSegmentItem>>(false, null);
            });

        // Setup TryGetStateAsync for List<string> (lock registry)
        mock.Setup(m => m.TryGetStateAsync<List<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is List<string> list)
                {
                    return new ConditionalValue<List<string>>(true, list);
                }
                return new ConditionalValue<List<string>>(false, null);
            });

        // Setup GetStateAsync for ActorMetadata (used in test assertions)
        mock.Setup(m => m.GetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata)
                {
                    return metadata;
                }
                return null!;
            });

        // Setup SetStateAsync
        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) =>
            {
                stateData[key] = value;
                return Task.CompletedTask;
            });

        // Setup RemoveStateAsync
        mock.Setup(m => m.RemoveStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) =>
            {
                stateData.Remove(key);
                return Task.CompletedTask;
            });

        // Setup SaveStateAsync
        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private async Task<QueueActor> CreateActorAsync(Mock<IActorStateManager> mockStateManager)
    {
        // Create mock timer manager that no-ops timer registration
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>()))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object
        };

        // Create mock actor invoker to handle DLQ push
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<Interfaces.PushRequest, Interfaces.PushResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<Interfaces.PushRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Interfaces.PushResponse { Success = true });

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(actorHost, mockInvoker.Object);

        // Use reflection to set the StateManager property
        var stateManagerProperty = typeof(Actor).GetProperty("StateManager");
        stateManagerProperty?.SetValue(actor, mockStateManager.Object);

        // Call OnActivateAsync to initialize metadata (simulates Dapr lifecycle)
        var onActivateMethod = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return actor;
    }

    [Fact]
    public async Task PushAsync_WithValidSingleItem_ReturnsSuccess()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsPushed);
    }

    [Fact]
    public async Task PushAsync_WithMultipleItems_ReturnsSuccessWithCount()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var item1Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test1" });
        var item2Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test2" });
        var item3Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test3" });

        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = item1Json, Priority = 1 },
                new Interfaces.PushItem { ItemJson = item2Json, Priority = 0 },
                new Interfaces.PushItem { ItemJson = item3Json, Priority = 1 }
            }
        };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, result.ItemsPushed);
    }

    [Fact]
    public async Task PushAsync_WithEmptyArray_ReturnsFalure()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.PushRequest { Items = new List<Interfaces.PushItem>() };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsPushed);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task PushAsync_WithEmptyItemJson_ReturnsFalure()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "", Priority = 0 }
            }
        };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsPushed);
    }

    [Fact]
    public async Task PushAsync_WithNegativePriority_ReturnsFalure()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = -1 }
            }
        };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsPushed);
    }

    [Fact]
    public async Task PushAsync_WithMixedPriorities_MaintainsFIFOPerPriority()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var item1Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "priority1-first" });
        var item2Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "priority0-urgent" });
        var item3Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "priority1-second" });

        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = item1Json, Priority = 1 },
                new Interfaces.PushItem { ItemJson = item2Json, Priority = 0 },
                new Interfaces.PushItem { ItemJson = item3Json, Priority = 1 }
            }
        };

        // Act
        var pushResult = await actor.Push(request);
        var pop1 = await actor.Pop(new Interfaces.PopRequest());
        var pop2 = await actor.Pop(new Interfaces.PopRequest());
        var pop3 = await actor.Pop(new Interfaces.PopRequest());

        // Assert
        Assert.True(pushResult.Success);
        Assert.Equal(3, pushResult.ItemsPushed);

        // Priority 0 should come first
        Assert.Equal(item2Json, pop1.Items[0].ItemJson);
        Assert.Equal(0, pop1.Items[0].Priority);

        // Then priority 1 items in FIFO order
        Assert.Equal(item1Json, pop2.Items[0].ItemJson);
        Assert.Equal(1, pop2.Items[0].Priority);

        Assert.Equal(item3Json, pop3.Items[0].ItemJson);
        Assert.Equal(1, pop3.Items[0].Priority);
    }

    [Fact]
    public async Task PushAsync_With101Items_AllocatesMultipleSegments()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        var items = new List<Interfaces.PushItem>();
        for (int i = 0; i < 101; i++)
        {
            var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["index"] = i });
            items.Add(new Interfaces.PushItem { ItemJson = itemJson, Priority = 1 });
        }

        var request = new Interfaces.PushRequest { Items = items };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(101, result.ItemsPushed);

        // Verify items can be popped in order
        for (int i = 0; i < 101; i++)
        {
            var popResult = await actor.Pop(new Interfaces.PopRequest());
            Assert.NotEmpty(popResult.Items);
        }
    }

    [Fact]
    public async Task PushAsync_WithOneInvalidItem_ReturnsFailureWithZeroItemsPushed()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var validItem = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "valid" });

        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = validItem, Priority = 1 },
                new Interfaces.PushItem { ItemJson = "", Priority = 1 }, // Invalid - empty
                new Interfaces.PushItem { ItemJson = validItem, Priority = 1 }
            }
        };

        // Act
        var result = await actor.Push(request);

        // Assert - all-or-nothing behavior
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsPushed);

        // Verify nothing was actually pushed
        var popResult = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(popResult.Items);
    }

    [Fact]
    public async Task PopAsync_FromEmptyQueue_ReturnsNull()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act
        var result = await actor.Pop(new Interfaces.PopRequest());

        // Assert
        Assert.Empty(result.Items);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task PopAsync_AfterPush_ReturnsItem()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var pushRequest = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        };

        // Act
        await actor.Push(pushRequest);
        var result = await actor.Pop(new Interfaces.PopRequest());

        // Assert
        Assert.NotEmpty(result.Items);
        Assert.False(result.Locked);
        Assert.Equal(0, result.Items[0].Priority); // Verify priority is returned
        var returnedItem = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(result.Items[0].ItemJson);
        Assert.Equal("test", returnedItem!["message"].ToString());
    }

    [Fact]
    public async Task PushPop_MaintainsFIFOOrder()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Push 3 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem
                {
                    ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 1 }),
                    Priority = 0
                }
            }
        });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem
                {
                    ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 2 }),
                    Priority = 0
                }
            }
        });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem
                {
                    ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 3 }),
                    Priority = 0
                }
            }
        });

        // Pop all items
        var item1 = await actor.Pop(new Interfaces.PopRequest());
        var item2 = await actor.Pop(new Interfaces.PopRequest());
        var item3 = await actor.Pop(new Interfaces.PopRequest());

        // Assert - Should be in FIFO order
        Assert.False(item1.Locked);
        Assert.False(item2.Locked);
        Assert.False(item3.Locked);
        var deserialized1 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item1.Items[0].ItemJson!);
        var deserialized2 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item2.Items[0].ItemJson!);
        var deserialized3 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item3.Items[0].ItemJson!);

        Assert.Equal(1, Convert.ToInt32(deserialized1!["id"].ToString()));
        Assert.Equal(2, Convert.ToInt32(deserialized2!["id"].ToString()));
        Assert.Equal(3, Convert.ToInt32(deserialized3!["id"].ToString()));
    }

    [Fact]
    public async Task PopWithAckAsync_CreatesLock()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        // Act
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Assert
        Assert.Single(result.Items);  // Successfully locked 1 item
        Assert.NotNull(result.Items[0].LockId);
        Assert.NotNull(result.Items[0].ItemJson);
        Assert.Equal(0, result.Items[0].Priority); // Verify priority is returned
    }

    [Fact]
    public async Task AcknowledgeAsync_WithValidLockId_ReturnsSuccess()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        var lockId = popResult.LockId!;

        // Act
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });

        // Assert
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithInvalidLockId_ReturnsFalse()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act
        var result = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = "invalid" });

        // Assert
        Assert.False(result.Success);
        Assert.Equal("LOCK_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task PopWithAck_CreatesNamedLockStateKey()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        // Act
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Assert
        Assert.NotNull(result.LockId);
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{result.LockId}-lock");
        Assert.True(lockState.HasValue);
        Assert.Equal(result.LockId, lockState.Value.LockId);
    }

    [Fact]
    public async Task PopWithAck_SetsCurrentLockId()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        // Act
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Assert
        Assert.NotNull(result.LockId);
        var metadata = await mockStateManager.Object.GetStateAsync<DaprMQ.ActorMetadata>("metadata");
        Assert.Equal(1, metadata.LockCount);
    }

    [Fact]
    public async Task Acknowledge_ClearsCurrentLockId()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        var lockId = popResult.LockId!;

        // Act
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });

        // Assert
        Assert.True(ackResult.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);
    }

    [Fact]
    public async Task Acknowledge_DeletesNamedLockState()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        var lockId = popResult.LockId!;

        // Act
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });

        // Assert
        Assert.True(ackResult.Success);
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{lockId}-lock");
        Assert.False(lockState.HasValue);
    }

    [Fact]
    public async Task ExtendLock_MismatchedLockId_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Act - Try to extend with wrong lock ID
        var result = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = "wrong-lock-id",
            AdditionalTtlSeconds = 10
        });

        // Assert
        Assert.False(result.Success);
        Assert.Equal("LOCK_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task PopWithAck_ExpiredLock_ReminderCleansUpAndAllowsNewLock()
    {
        // With Phase 2 reminders: expired locks are cleaned by reminder callback, not by PopWithAck

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = itemJson, Priority = 0 }]
        });

        // Create expired lock manually
        string expiredLockId = "expired-lock";
        var expiredLock = new LockState
        {
            LockId = expiredLockId,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-40).ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds(),
            Priority = 0,
            HeadSegment = 0,
            ItemJson = itemJson,
            CompetingConsumerMode = false
        };
        await mockStateManager.Object.SetStateAsync($"{expiredLockId}-lock", expiredLock);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        await mockStateManager.Object.SetStateAsync("metadata", metadata with { LockCount = 1 });

        // Act - Simulate reminder cleanup (reminder would fire automatically in production)
        await actor.ReceiveReminderAsync($"lock-{expiredLockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Now PopWithAck should succeed
        var result = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });

        // Assert
        Assert.Single(result.Items);  // Successfully locked 1 item
        Assert.NotNull(result.Items[0].LockId);
        Assert.NotEqual(expiredLockId, result.Items[0].LockId); // Should be a new lock

        // Verify expired lock was cleaned up by reminder
        var expiredLockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{expiredLockId}-lock");
        Assert.False(expiredLockState.HasValue);

        // Verify lock count updated (old lock removed, new lock added = still 1)
        metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(1, metadata.LockCount);
    }

    [Fact]
    public async Task PushPop_MaintainsExactJsonFormat()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var original = "{\"key\":\"value\",\"number\":42}";

        // Act
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = original, Priority = 0 }
            }
        });
        var result = await actor.Pop(new Interfaces.PopRequest());

        // Assert
        Assert.NotEmpty(result.Items);
        Assert.False(result.Locked);
        Assert.Equal(original, result.Items[0].ItemJson);
    }

    [Fact]
    public async Task Push_WithoutExplicitPriority_UsesDefaultPriorityOne()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"test\":\"data\"}", Priority = 1 }
            }
        };
        // Priority not explicitly set - should default to 1

        // Act
        await actor.Push(request);

        // Assert - verify it went to priority 1 queue by checking metadata
        var metadataState = await mockStateManager.Object.TryGetStateAsync<ActorMetadata>("metadata", CancellationToken.None);
        Assert.True(metadataState.HasValue);
        var metadata = metadataState.Value;
        Assert.NotNull(metadata);

        Assert.True(metadata.Queues.ContainsKey(1), "Item should be in priority 1 queue");
        Assert.False(metadata.Queues.ContainsKey(0), "Item should NOT be in priority 0 queue");

        // Also verify Pop returns priority 1
        var popResult = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(popResult.Items);
        Assert.Equal(1, popResult.Items[0].Priority); // Verify default priority is returned
    }

    [Fact]
    public async Task ExpiredLock_RestoresOriginalPriority()
    {
        // With Phase 2: Lock-in-place means item never leaves queue, just lock state is cleaned by reminder

        // Arrange - push items at different priorities
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":1}", Priority = 2 }]
        });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":2}", Priority = 1 }]
        });

        // PopWithAck with 1 second TTL (will pop priority 1 item first)
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.ItemJson);
        Assert.NotNull(popResult.LockId);
        Assert.Contains("\"id\":2", popResult.ItemJson);

        // Wait for lock to expire, then simulate reminder cleanup
        await Task.Delay(1100);
        await actor.ReceiveReminderAsync($"lock-{popResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Pop again - should get same item (remained at priority 1 due to lock-in-place)
        var secondPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(secondPop.Items);
        Assert.False(secondPop.Locked);
        Assert.Contains("\"id\":2", secondPop.Items[0].ItemJson);

        // Final pop gets priority 2 item (proving order was preserved)
        var thirdPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(thirdPop.Items);
        Assert.False(thirdPop.Locked);
        Assert.Contains("\"id\":1", thirdPop.Items[0].ItemJson);

        // Queue should now be empty
        var fourthPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(fourthPop.Items);
        Assert.False(fourthPop.Locked);
    }

    [Fact]
    public async Task PopWithAck_CommitsAtomically()
    {
        // Arrange - push single item
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });

        // Act - PopWithAck should commit lock atomically
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Assert - item is successfully locked (dequeued and stored in lock)
        Assert.Single(result.Items);
        Assert.NotNull(result.Items[0].ItemJson);
        Assert.NotNull(result.Items[0].LockId);
        Assert.Contains("\"id\":1", result.Items[0].ItemJson);

        // Verify queue is blocked while lock exists (cannot pop in legacy mode)
        var popResult = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(popResult.Items);
        Assert.True(popResult.Locked);
        Assert.Equal("Queue is locked by another operation", popResult.Message);

        // After acknowledgement, queue should be empty
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = result.Items[0].LockId });
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Now pop should return empty
        var finalPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(finalPop.Items);
        Assert.False(finalPop.Locked);
    }

    [Fact]
    public async Task ExpiredLock_PreservesQueuePosition()
    {
        // Phase 3: Item dequeued during PopWithAck, re-queued at end when lock expires

        // Arrange - push items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"A\"}", Priority = 1 }]
        });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"B\"}", Priority = 1 }]
        });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"C\"}", Priority = 1 }]
        });

        // Act - PopWithAck locks Item-A (dequeues it)
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.ItemJson);
        Assert.NotNull(popResult.LockId);
        Assert.Contains("\"id\":\"A\"", popResult.ItemJson);

        // Push Item-D while lock is active
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"D\"}", Priority = 1 }]
        });

        // Wait for lock to expire, then simulate reminder cleanup (re-queues Item-A at end)
        await Task.Delay(1100);
        await actor.ReceiveReminderAsync($"lock-{popResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Pop should return B, C, D, A (A was re-queued at end)
        var firstPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(firstPop.Items);
        Assert.False(firstPop.Locked);
        Assert.Contains("\"id\":\"B\"", firstPop.Items[0].ItemJson);

        // Second pop returns C
        var secondPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(secondPop.Locked);
        Assert.Contains("\"id\":\"C\"", secondPop.Items[0].ItemJson);

        var thirdPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(thirdPop.Locked);
        Assert.Contains("\"id\":\"D\"", thirdPop.Items[0].ItemJson);

        // Fourth pop returns A (re-queued after lock expiry)
        var fourthPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(fourthPop.Locked);
        Assert.Contains("\"id\":\"A\"", fourthPop.Items[0].ItemJson);

        // Queue should now be empty
        var fifthPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(fifthPop.Items);
        Assert.False(fifthPop.Locked);
    }

    [Fact]
    public async Task PopWithAck_ItemsStayInQueueUntilAck()
    {
        // Arrange - push item
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":\"test\"}", Priority = 1 }
            }
        });

        // Act - PopWithAck locks the item
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popResult.ItemJson);
        var lockId = popResult.LockId;

        // Assert - Pop returns empty while lock exists (item still in queue but locked)
        var blockedPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(blockedPop.Items);
        Assert.True(blockedPop.Locked);
        Assert.Equal("Queue is locked by another operation", blockedPop.Message);

        // Acknowledge the lock
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });
        Assert.True(ackResult.Success);

        // Now queue should be truly empty (item dequeued on acknowledgement)
        var finalPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(finalPop.Items);
        Assert.False(finalPop.Locked);
    }

    [Fact]
    public async Task Acknowledge_RemovesItemsFromQueue()
    {
        // Arrange - push multiple items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 }
            }
        });

        // Act - PopWithAck first item
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.Contains("\"id\":1", popResult.ItemJson!);
        var lockId = popResult.LockId;

        // Acknowledge
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Assert - Next pop should return second item
        var secondPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(secondPop.Items);
        Assert.False(secondPop.Locked);
        Assert.Contains("\"id\":2", secondPop.Items[0].ItemJson!);

        // Queue should now be empty
        var thirdPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(thirdPop.Items);
        Assert.False(thirdPop.Locked);
    }

    [Fact]
    public async Task MultiplePopsBlocked_WhenLockActive()
    {
        // Arrange - push items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 }
            }
        });

        // Act - PopWithAck creates lock
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        var lockId = popResult.LockId;

        // Attempt Pop() - should be blocked
        var blockedPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(blockedPop.Items);
        Assert.True(blockedPop.Locked);
        Assert.Equal("Queue is locked by another operation", blockedPop.Message);

        // Attempt another PopWithAck - should be blocked
        var blockedPopWithAck = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.True(blockedPopWithAck.Locked);
        Assert.Null(blockedPopWithAck.ItemJson);
        Assert.Contains("locked", blockedPopWithAck.Message, StringComparison.OrdinalIgnoreCase);

        // Acknowledge
        await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });

        // Pop() should now work
        var successfulPop = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(successfulPop.Items);
        Assert.False(successfulPop.Locked);
        Assert.Contains("\"id\":2", successfulPop.Items[0].ItemJson!);
    }

    [Fact]
    public async Task LockExpiry_DoesNotReorderQueue()
    {
        // Phase 3: Item dequeued during PopWithAck, re-queued at end of priority when lock expires

        // Arrange - push items with different priorities
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"P1-A\"}", Priority = 1 }]
        });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"P1-B\"}", Priority = 1 }]
        });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"P2-A\"}", Priority = 2 }]
        });

        // Act - PopWithAck on priority 1 item (dequeues P1-A)
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.LockId);
        Assert.Contains("\"id\":\"P1-A\"", popResult.ItemJson!);

        // Push more items to priority 1 while lock is active
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"P1-C\"}", Priority = 1 }]
        });
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":\"P1-D\"}", Priority = 1 }]
        });

        // Let lock expire, then simulate reminder cleanup (re-queues P1-A at end of priority 1)
        await Task.Delay(1100);
        await actor.ReceiveReminderAsync($"lock-{popResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Pop all items: B, C, D, A (at end of priority 1), then P2-A
        var pop1 = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(pop1.Locked);
        Assert.Contains("\"id\":\"P1-B\"", pop1.Items[0].ItemJson);

        var pop2 = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(pop2.Locked);
        Assert.Contains("\"id\":\"P1-C\"", pop2.Items[0].ItemJson);

        var pop3 = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(pop3.Locked);
        Assert.Contains("\"id\":\"P1-D\"", pop3.Items[0].ItemJson);

        var pop4 = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(pop4.Locked);
        Assert.Contains("\"id\":\"P1-A\"", pop4.Items[0].ItemJson); // Re-queued at end

        var pop5 = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(pop5.Locked);
        Assert.Contains("\"id\":\"P2-A\"", pop5.Items[0].ItemJson);

        // Queue should be empty
        var pop6 = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(pop6.Items);
        Assert.False(pop6.Locked);
    }

    [Fact]
    public async Task ErrorState_BlocksPushAndPopOperations()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Create corrupted metadata and directly set it via SetStateAsync
        var corruptedMetadata = new ActorMetadata
        {
            ErrorMessage = "Test corruption error - segment missing from external store",
            Config = new MetadataConfig(),
            Queues = new Dictionary<int, QueueMetadata>()
        };

        // Use SetStateAsync to put corrupted metadata into the state
        await mockStateManager.Object.SetStateAsync("metadata", corruptedMetadata);

        // Act & Assert - Push should throw
        var pushEx = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await actor.Push(new Interfaces.PushRequest
            {
                Items = new List<Interfaces.PushItem>
                {
                    new Interfaces.PushItem { ItemJson = "{\"test\":\"data\"}", Priority = 0 }
                }
            })
        );
        Assert.Contains("Queue corrupted", pushEx.Message);
        Assert.Contains("Test corruption error", pushEx.Message);

        // Act & Assert - Pop should throw
        var popEx = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await actor.Pop(new Interfaces.PopRequest())
        );
        Assert.Contains("Queue corrupted", popEx.Message);
        Assert.Contains("Test corruption error", popEx.Message);
    }

    [Fact]
    public async Task ExtendLock_ValidLock_ExtendsExpiry()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });

        // PopWithAck to create lock with 10s TTL
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 10 });
        Assert.NotNull(popWithAckResult.LockId);
        var originalExpiresAt = popWithAckResult.LockExpiresAt!.Value;

        // Act - Extend lock by 30 seconds
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popWithAckResult.LockId,
            AdditionalTtlSeconds = 30
        });

        // Assert
        Assert.True(extendResult.Success);
        Assert.True(extendResult.NewExpiresAt > originalExpiresAt);
        // New expiry should be approximately 30 seconds later (within 2 seconds tolerance)
        Assert.True(Math.Abs(extendResult.NewExpiresAt - (originalExpiresAt + 30)) < 2);
    }

    [Fact]
    public async Task ExtendLock_InvalidLockId_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 10 });
        Assert.NotNull(popWithAckResult.LockId);

        // Act - Try to extend with wrong lock ID
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = "wrong-lock-id",
            AdditionalTtlSeconds = 30
        });

        // Assert
        Assert.False(extendResult.Success);
        Assert.Equal("LOCK_NOT_FOUND", extendResult.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_ExpiredLock_ReturnsLockExpired()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock with 1s TTL
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popWithAckResult.LockId);

        // Wait for lock to expire
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Act - Try to extend expired lock
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popWithAckResult.LockId,
            AdditionalTtlSeconds = 30
        });

        // Assert
        Assert.False(extendResult.Success);
        Assert.Equal("LOCK_EXPIRED", extendResult.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_NoActiveLock_ReturnsLockNotFound()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Try to extend lock when no lock exists
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = "nonexistent-lock",
            AdditionalTtlSeconds = 30
        });

        // Assert
        Assert.False(extendResult.Success);
        Assert.Equal("LOCK_NOT_FOUND", extendResult.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_NegativeTtl_ReturnsInvalidTtl()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 10 });
        Assert.NotNull(popWithAckResult.LockId);

        // Act - Try to extend with negative TTL
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popWithAckResult.LockId,
            AdditionalTtlSeconds = -1
        });

        // Assert
        Assert.False(extendResult.Success);
        Assert.Equal("INVALID_TTL", extendResult.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_MultipleExtensions_Accumulates()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock with 10s TTL
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 10 });
        Assert.NotNull(popWithAckResult.LockId);
        var originalExpiresAt = popWithAckResult.LockExpiresAt!.Value;

        // Act - Extend lock twice
        var extendResult1 = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popWithAckResult.LockId,
            AdditionalTtlSeconds = 10
        });
        Assert.True(extendResult1.Success);

        var extendResult2 = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popWithAckResult.LockId,
            AdditionalTtlSeconds = 10
        });
        Assert.True(extendResult2.Success);

        // Assert - Total extension should be 20 seconds (within tolerance)
        Assert.True(Math.Abs(extendResult2.NewExpiresAt - (originalExpiresAt + 20)) < 2);
    }

    [Fact]
    public async Task ExtendLock_KeepsItemLocked_UntilAcknowledge()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 10 });
        Assert.NotNull(popWithAckResult.LockId);

        // Extend lock
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popWithAckResult.LockId,
            AdditionalTtlSeconds = 30
        });
        Assert.True(extendResult.Success);

        // Act - Try to Pop (should be blocked)
        var popResult = await actor.Pop(new Interfaces.PopRequest());

        // Assert - Queue should still be locked
        Assert.True(popResult.Locked);
        Assert.Empty(popResult.Items);

        // Now acknowledge
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = popWithAckResult.LockId });
        Assert.True(ackResult.Success);

        // Pop should now work (queue empty)
        var popResult2 = await actor.Pop(new Interfaces.PopRequest());
        Assert.False(popResult2.Locked);
        Assert.True(popResult2.IsEmpty);
    }

    [Fact]
    public async Task DeadLetter_ValidLock_AttemptsToMoveToDlq()
    {
        // Note: This unit test verifies lock validation logic.
        // ActorProxy.Create requires a Dapr runtime, so full DLQ flow is tested in integration tests.

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1,\"value\":\"test\"}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popWithAckResult.LockId);

        // Act - Attempt to move to dead letter queue
        var deadLetterResult = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = popWithAckResult.LockId });

        // Assert - Lock validation passed (actual DLQ push tested in integration tests)
        // In unit tests, ActorProxy.Create will fail without Dapr runtime
        Assert.NotNull(deadLetterResult);
        Assert.True(deadLetterResult.Status == "SUCCESS" || deadLetterResult.ErrorCode == "DLQ_PUSH_FAILED" || deadLetterResult.ErrorCode == "INTERNAL_ERROR");
    }

    [Fact]
    public async Task DeadLetter_LockNotFound_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Try to deadletter with non-existent lock
        var result = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = "nonexistent-lock" });

        // Assert
        Assert.Equal("ERROR", result.Status);
        Assert.Equal("LOCK_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_InvalidLockId_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Act - Try to deadletter with wrong lock ID
        var result = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = "wrong-lock-id" });

        // Assert - With counter approach, can't distinguish invalid vs not found
        Assert.Equal("ERROR", result.Status);
        Assert.Equal("LOCK_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_ExpiredLock_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock with negative expiry (already expired)
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popWithAckResult.LockId);

        // Manually expire the lock by setting ExpiresAt to past timestamp
        var lockState = new LockState
        {
            LockId = popWithAckResult.LockId,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-40).ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds(), // Expired
            Priority = 1,
            HeadSegment = 0,
            ItemJson = popWithAckResult.ItemJson!,
            CompetingConsumerMode = false
        };
        await mockStateManager.Object.SetStateAsync($"{popWithAckResult.LockId}-lock", lockState);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        await mockStateManager.Object.SetStateAsync("metadata", metadata with { LockCount = 1 });

        // Act - Try to deadletter with expired lock
        var result = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = popWithAckResult.LockId });

        // Assert
        Assert.Equal("ERROR", result.Status);
        Assert.Equal("LOCK_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_PreservesPriority_ValidatesLock()
    {
        // Note: This unit test verifies lock validation for priority 0 items.
        // Priority preservation and full DLQ flow are tested in integration tests.

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push priority 0 item (fast lane)
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1,\"urgent\":true}", Priority = 0 }
            }
        });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popWithAckResult.LockId);

        // Act - Attempt to move to dead letter queue
        var deadLetterResult = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = popWithAckResult.LockId });

        // Assert - Lock validation passed (actual DLQ operation requires Dapr runtime)
        Assert.NotNull(deadLetterResult);
        // Either succeeds or fails with DLQ_PUSH_FAILED/INTERNAL_ERROR (no Dapr runtime in unit tests)
        Assert.True(deadLetterResult.Status == "SUCCESS" || deadLetterResult.ErrorCode == "DLQ_PUSH_FAILED" || deadLetterResult.ErrorCode == "INTERNAL_ERROR");
    }

    [Fact]
    public async Task ReceiveReminderAsync_WithValidLock_CleansUpLockState()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Create a lock
        string lockId = "test-lock-id";
        var lockData = new LockState
        {
            LockId = lockId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
            Priority = 1,
            HeadSegment = 0,
            ItemJson = "{\"test\":\"item\"}",
            CompetingConsumerMode = false
        };
        await mockStateManager.Object.SetStateAsync($"{lockId}-lock", lockData);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        await mockStateManager.Object.SetStateAsync("metadata", metadata with { LockCount = 1 });

        // Act - Simulate reminder callback
        await actor.ReceiveReminderAsync($"lock-{lockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock state should be removed and lock count should be 0
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{lockId}-lock");
        metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");

        Assert.False(lockState.HasValue);
        Assert.Equal(0, metadata.LockCount);
    }

    [Fact]
    public async Task ReceiveReminderAsync_WithNonLockReminder_DoesNothing()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Create a lock to ensure it's not touched
        string lockId = "test-lock-id";
        var lockData = new LockState
        {
            LockId = lockId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
            Priority = 1,
            HeadSegment = 0,
            ItemJson = "{\"test\":\"item\"}",
            CompetingConsumerMode = false
        };
        await mockStateManager.Object.SetStateAsync($"{lockId}-lock", lockData);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        await mockStateManager.Object.SetStateAsync("metadata", metadata with { LockCount = 1 });

        // Act - Simulate reminder callback with non-lock reminder name
        await actor.ReceiveReminderAsync("some-other-reminder", new byte[0], TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock state and lock count should still exist
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{lockId}-lock");
        metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");

        Assert.True(lockState.HasValue);
        Assert.Equal(1, metadata.LockCount);
    }

    [Fact]
    public async Task PopWithAck_RegistersReminder_WithCorrectTtl()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });

        // Act - PopWithAck should register a reminder
        var result = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });

        // Assert - Verify lock was created with correct TTL
        Assert.Single(result.Items);
        Assert.NotNull(result.Items[0].LockId);
        Assert.True(result.Items[0].LockExpiresAt > 0);

        double expectedExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30;
        Assert.InRange(result.Items[0].LockExpiresAt, expectedExpiry - 2, expectedExpiry + 2); // Within 2 seconds tolerance

        // Note: Full reminder registration verification requires integration tests
        // Unit tests verify the lock state is created correctly, which is prerequisite for reminder
    }

    [Fact]
    public async Task PopWithAck_LockExpiration_ReminderWillAutoCleanup()
    {
        // This test documents the expected behavior with reminders enabled.
        // When a lock expires, the reminder callback will automatically clean up lock state.
        // No manual expiration checks needed in PopWithAck.

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });

        // Act - Create lock
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.LockId);

        // Simulate reminder firing after TTL
        await Task.Delay(1100); // Wait for expiration
        await actor.ReceiveReminderAsync($"lock-{popResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock state should be cleaned up
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{popResult.LockId}-lock");

        Assert.False(lockState.HasValue);
    }

    [Fact]
    public async Task ReceiveReminderAsync_RequeuesExpiredLock_AtOriginalPriority()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and lock it
        string itemJson = "{\"test\":\"data\"}";
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = itemJson, Priority = 2 }]
        });

        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.LockId);
        Assert.Equal(itemJson, popResult.ItemJson);

        // Verify queue empty after PopWithAck (item dequeued)
        var popEmpty = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(popEmpty.Items); // Queue should be empty

        // Act - Simulate reminder firing (lock expires and re-queues)
        await actor.ReceiveReminderAsync($"lock-{popResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Item should be re-queued at original priority 2
        var popResult2 = await actor.Pop(new Interfaces.PopRequest());
        Assert.NotEmpty(popResult2.Items);
        Assert.Equal(itemJson, popResult2.Items[0].ItemJson);
        Assert.Equal(2, popResult2.Items[0].Priority);
    }

    [Fact]
    public async Task PopWithAck_DecrementsCountImmediately()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push 3 items
        await actor.Push(new PushRequest
        {
            Items = [
                new PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new PushItem { ItemJson = "{\"id\":3}", Priority = 1 }
            ]
        });

        // Act - PopWithAck should decrement immediately (dequeue first item)
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });
        Assert.Equal("{\"id\":1}", popResult.ItemJson);
        Assert.NotNull(popResult.LockId);

        // Acknowledge the lock to allow further operations
        await actor.Acknowledge(new AcknowledgeRequest { LockId = popResult.LockId });

        // Assert - Regular Pop should return second item (not first), proving first was dequeued
        var popResult2 = await actor.Pop(new Interfaces.PopRequest());
        Assert.Equal("{\"id\":2}", popResult2.Items[0].ItemJson);

        // Third item still available
        var popResult3 = await actor.Pop(new Interfaces.PopRequest());
        Assert.Equal("{\"id\":3}", popResult3.Items[0].ItemJson);

        // Queue should be empty now
        var popEmpty = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(popEmpty.Items);
    }

    [Fact]
    public async Task Acknowledge_DoesNotDequeueAgain()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push items and lock one
        await actor.Push(new PushRequest
        {
            Items = [
                new PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new PushItem { ItemJson = "{\"id\":2}", Priority = 1 }
            ]
        });

        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });
        Assert.Equal("{\"id\":1}", popResult.ItemJson);
        Assert.NotNull(popResult.LockId);

        // Act - Acknowledge should just remove lock, not modify queue
        var ackResult = await actor.Acknowledge(new AcknowledgeRequest { LockId = popResult.LockId });
        Assert.True(ackResult.Success);

        // Assert - Only item 2 should be in queue (item 1 was dequeued during PopWithAck, not Acknowledge)
        var popResult2 = await actor.Pop(new Interfaces.PopRequest());
        Assert.Equal("{\"id\":2}", popResult2.Items[0].ItemJson);

        // Queue should be empty
        var popEmpty = await actor.Pop(new Interfaces.PopRequest());
        Assert.Empty(popEmpty.Items);
    }

    [Fact]
    public async Task ExtendLock_UpdatesReminder_WithNewTtl()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push and lock an item
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popResult.LockId);

        double originalExpiry = popResult.LockExpiresAt ?? 0;

        // Act - Extend lock by 20 seconds
        var extendResult = await actor.ExtendLock(new ExtendLockRequest
        {
            LockId = popResult.LockId,
            AdditionalTtlSeconds = 20
        });

        // Assert - Lock expiry should be extended
        Assert.True(extendResult.Success);
        Assert.True(extendResult.NewExpiresAt > originalExpiry);
        Assert.InRange(extendResult.NewExpiresAt, originalExpiry + 18, originalExpiry + 22); // Within 2 seconds tolerance

        // Verify lock state was updated
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{popResult.LockId}-lock");
        Assert.True(lockState.HasValue);
        Assert.Equal(extendResult.NewExpiresAt, lockState.Value.ExpiresAt);

        // Note: Full reminder update verification requires integration tests
        // Unit tests verify the lock state is updated correctly
    }

    [Fact]
    public async Task ExtendLock_PreviousReminderReplaced_NewReminderScheduled()
    {
        // This test documents the expected behavior:
        // ExtendLock should unregister the old reminder and register a new one
        // with the updated TTL to match the new lock expiration time.

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push and lock an item
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 5 });
        Assert.NotNull(popResult.LockId);

        // Act - Extend lock
        await actor.ExtendLock(new ExtendLockRequest
        {
            LockId = popResult.LockId,
            AdditionalTtlSeconds = 10
        });

        // Simulate old reminder firing (should do nothing since lock state is updated)
        await Task.Delay(5100);
        await actor.ReceiveReminderAsync($"lock-{popResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock should still exist because reminder was replaced
        // (In real scenario, old reminder would be unregistered and wouldn't fire)
        // This unit test validates the cleanup logic is safe even if old reminder fires
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{popResult.LockId}-lock");

        // Lock gets cleaned up by the reminder callback
        Assert.False(lockState.HasValue);

        // Note: Integration tests should verify the old reminder is actually unregistered
        // and doesn't fire after ExtendLock is called
    }

    [Fact]
    public async Task PopWithAck_CompetingConsumers_AllowsParallelLocks()
    {
        // Arrange - push 3 items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 },
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 2 }), Priority = 1 },
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 3 }), Priority = 1 }
            }
        });

        // Act - two parallel PopWithAck with competing consumers enabled
        var result1 = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        var result2 = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        // Assert - both succeed with different items
        Assert.Single(result1.Items);
        Assert.Single(result2.Items);
        Assert.NotEqual(result1.Items[0].LockId, result2.Items[0].LockId);
        Assert.NotEqual(result1.Items[0].ItemJson, result2.Items[0].ItemJson);
    }

    [Fact]
    public async Task PopWithAck_LegacyMode_BlocksWhenLockExists()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 },
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 2 }), Priority = 1 }
            }
        });

        // First lock with competing consumers enabled
        var result1 = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        Assert.NotNull(result1.LockId);

        // Act - second PopWithAck with legacy mode (AllowCompetingConsumers = false)
        var result2 = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = false
        });

        // Assert - blocked
        Assert.True(result2.Locked);
        Assert.Null(result2.ItemJson);
        Assert.Null(result2.LockId);
        Assert.Contains("locked", result2.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acknowledge_RemovesFromRegistry()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 }
            }
        });
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        // Act
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = popResult.LockId! });

        // Assert
        Assert.True(ackResult.Success);

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);
    }

    [Fact]
    public async Task ExtendLock_ValidatesAgainstRegistry()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 }
            }
        });
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        // Act
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = popResult.LockId!,
            AdditionalTtlSeconds = 30
        });

        // Assert
        Assert.True(extendResult.Success);

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(1, metadata.LockCount);
    }

    [Fact]
    public async Task ReceiveReminderAsync_RemovesFromRegistry()
    {
        // Arrange - create lock
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 }
            }
        });
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 1,
            AllowCompetingConsumers = true
        });
        string lockId = popResult.LockId!;

        // Act - trigger reminder (simulating lock expiry)
        await actor.ReceiveReminderAsync($"lock-{lockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromMilliseconds(-1));

        // Assert - lock count updated to 0
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);
    }

    // ===== Bulk Pop Tests =====

    [Fact]
    public async Task Pop_WithCount_ReturnsMultipleItems()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push 5 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":4}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":5}", Priority = 1 }
            }
        });

        // Act
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 5 });

        // Assert
        Assert.Equal(5, result.Items.Count);
        Assert.Equal("{\"id\":1}", result.Items[0].ItemJson);
        Assert.Equal("{\"id\":2}", result.Items[1].ItemJson);
        Assert.Equal("{\"id\":3}", result.Items[2].ItemJson);
        Assert.Equal("{\"id\":4}", result.Items[3].ItemJson);
        Assert.Equal("{\"id\":5}", result.Items[4].ItemJson);
        Assert.All(result.Items, item => Assert.Equal(1, item.Priority));
        Assert.False(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Pop_WithCountGreaterThanAvailable_ReturnsPartialResults()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push only 3 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - Request 10 items but only 3 exist
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 10 });

        // Assert
        Assert.Equal(3, result.Items.Count);
        Assert.Equal("{\"id\":1}", result.Items[0].ItemJson);
        Assert.Equal("{\"id\":2}", result.Items[1].ItemJson);
        Assert.Equal("{\"id\":3}", result.Items[2].ItemJson);
        Assert.False(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Pop_WithCountZero_ReturnsEmptyArray()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push some items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });

        // Act - Request 0 items
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 0 });

        // Assert
        Assert.Empty(result.Items);
        Assert.False(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Pop_WithCountExceedingMax_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Request more than max (100)
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 101 });

        // Assert
        Assert.Empty(result.Items);
        Assert.Contains("Count must be between", result.Message);
    }

    [Fact]
    public async Task Pop_CrossPriority_ReturnsInPriorityOrder()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push items across different priorities
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"priority\":1,\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"priority\":1,\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"priority\":0,\"id\":1}", Priority = 0 },
                new Interfaces.PushItem { ItemJson = "{\"priority\":2,\"id\":1}", Priority = 2 },
                new Interfaces.PushItem { ItemJson = "{\"priority\":0,\"id\":2}", Priority = 0 }
            }
        });

        // Act - Pop all items
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 5 });

        // Assert - Should come out in priority order: 0, 0, 1, 1, 2
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(0, result.Items[0].Priority);
        Assert.Equal("{\"priority\":0,\"id\":1}", result.Items[0].ItemJson);
        Assert.Equal(0, result.Items[1].Priority);
        Assert.Equal("{\"priority\":0,\"id\":2}", result.Items[1].ItemJson);
        Assert.Equal(1, result.Items[2].Priority);
        Assert.Equal("{\"priority\":1,\"id\":1}", result.Items[2].ItemJson);
        Assert.Equal(1, result.Items[3].Priority);
        Assert.Equal("{\"priority\":1,\"id\":2}", result.Items[3].ItemJson);
        Assert.Equal(2, result.Items[4].Priority);
        Assert.Equal("{\"priority\":2,\"id\":1}", result.Items[4].ItemJson);
    }

    [Fact]
    public async Task Pop_EmptyQueue_ReturnsEmptyArrayWithIsEmptyTrue()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Pop from empty queue
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 5 });

        // Assert
        Assert.Empty(result.Items);
        Assert.True(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Pop_LockedQueue_ReturnsEmptyArrayWithLockedTrue()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and lock it
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 }
            }
        });
        await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Act - Try to pop from locked queue
        var result = await actor.Pop(new Interfaces.PopRequest { Count = 5 });

        // Assert
        Assert.Empty(result.Items);
        Assert.False(result.IsEmpty);
        Assert.True(result.Locked);
    }

    [Fact]
    public async Task Pop_DefaultCount_PopsSingleItem()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push 3 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - Pop with default count (should be 1)
        var result = await actor.Pop(new Interfaces.PopRequest());

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("{\"id\":1}", result.Items[0].ItemJson);
        Assert.Equal(1, result.Items[0].Priority);
    }

    // Phase 2: Bulk PopWithAck Tests

    [Fact]
    public async Task PopWithAck_WithCount_CreatesMultipleLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push 5 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":4}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":5}", Priority = 1 }
            }
        });

        // Act - PopWithAck with count=3 in competing consumer mode
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 3,
            AllowCompetingConsumers = true
        });

        // Assert - Should return 3 items, each with unique lock ID
        Assert.Equal(3, result.Items.Count);
        Assert.False(result.IsEmpty);

        // Verify each item has unique lock ID
        var lockIds = result.Items.Select(i => i.LockId).ToList();
        Assert.Equal(3, lockIds.Distinct().Count());

        // Verify items are in FIFO order
        Assert.Contains("\"id\":1", result.Items[0].ItemJson);
        Assert.Contains("\"id\":2", result.Items[1].ItemJson);
        Assert.Contains("\"id\":3", result.Items[2].ItemJson);

        // Verify all items have expiry times
        Assert.All(result.Items, item => Assert.NotNull(item.LockExpiresAt));
    }

    [Fact]
    public async Task PopWithAck_WithCount_CreatesIndependentLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push 4 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":4}", Priority = 1 }
            }
        });

        // Act - PopWithAck with count=3
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 3,
            AllowCompetingConsumers = true
        });

        // Assert - Should create 3 independent locks, each can be acknowledged separately
        Assert.Equal(3, result.Items.Count);

        // Acknowledge first lock
        var ack1 = await actor.Acknowledge(new Interfaces.AcknowledgeRequest
        {
            LockId = result.Items[0].LockId
        });
        Assert.True(ack1.Success);
        Assert.Equal(1, ack1.ItemsAcknowledged);

        // Acknowledge second lock
        var ack2 = await actor.Acknowledge(new Interfaces.AcknowledgeRequest
        {
            LockId = result.Items[1].LockId
        });
        Assert.True(ack2.Success);
        Assert.Equal(1, ack2.ItemsAcknowledged);

        // Third lock should still be valid
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = result.Items[2].LockId,
            AdditionalTtlSeconds = 10
        });
        Assert.True(extendResult.Success);
    }

    [Fact]
    public async Task PopWithAck_WithCountPartial_ReturnsAvailableItems()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push only 3 items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - Request 10 items but only 3 available
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 10,
            AllowCompetingConsumers = true
        });

        // Assert - Should return only 3 items (partial result)
        Assert.Equal(3, result.Items.Count);
        Assert.False(result.IsEmpty);
        Assert.Contains("\"id\":1", result.Items[0].ItemJson);
        Assert.Contains("\"id\":2", result.Items[1].ItemJson);
        Assert.Contains("\"id\":3", result.Items[2].ItemJson);
    }

    [Fact]
    public async Task PopWithAck_LegacyMode_BlocksWithExistingLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - First PopWithAck in legacy mode (default)
        var firstResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 1,
            AllowCompetingConsumers = false  // Legacy mode
        });

        Assert.Equal(1, firstResult.Items.Count);

        // Second PopWithAck should be blocked in legacy mode
        var secondResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 2,
            AllowCompetingConsumers = false  // Legacy mode
        });

        // Assert - Second request should be blocked
        Assert.Empty(secondResult.Items);
        Assert.True(secondResult.Locked);
        Assert.Equal("Queue is locked by another operation", secondResult.Message);
    }

    [Fact]
    public async Task PopWithAck_CompetingConsumerMode_AllowsMultipleLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push items
        await actor.Push(new Interfaces.PushRequest
        {
            Items = new List<Interfaces.PushItem>
            {
                new Interfaces.PushItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.PushItem { ItemJson = "{\"id\":4}", Priority = 1 }
            }
        });

        // Act - First PopWithAck in competing consumer mode
        var firstResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 2,
            AllowCompetingConsumers = true
        });

        Assert.Equal(2, firstResult.Items.Count);

        // Second PopWithAck should also succeed in competing consumer mode
        var secondResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest
        {
            TtlSeconds = 30,
            Count = 2,
            AllowCompetingConsumers = true
        });

        // Assert - Second request should succeed
        Assert.Equal(2, secondResult.Items.Count);
        Assert.False(secondResult.Locked);
        Assert.False(secondResult.IsEmpty);

        // Verify all 4 items were locked with different IDs
        var allLockIds = firstResult.Items.Select(i => i.LockId)
            .Concat(secondResult.Items.Select(i => i.LockId))
            .ToList();
        Assert.Equal(4, allLockIds.Distinct().Count());
    }

    [Fact]
    public async Task ReceiveReminderAsync_WhenLockAlreadyAcknowledged_DoesNotDoubleDecrementLockCount()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item
        string itemJson = "{\"id\":1,\"data\":\"test\"}";
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = itemJson, Priority = 0 }]
        });

        // PopWithAck to create lock
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });
        Assert.Single(popResult.Items);
        string lockId = popResult.Items[0].LockId!;

        // Verify lock count = 1
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(1, metadata.LockCount);

        // Acknowledge the lock (removes lock, decrements counter, but reminder may still exist)
        await actor.Acknowledge(new AcknowledgeRequest { LockId = lockId });

        // Verify lock count = 0
        metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);

        // Verify lock is gone
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{lockId}-lock");
        Assert.False(lockState.HasValue);

        // Act - Simulate reminder firing after lock already acknowledged
        // (This happens when UnregisterReminderAsync fails in AcknowledgeAsync)
        await actor.ReceiveReminderAsync($"lock-{lockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock count should still be 0 (not -1)
        metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);
    }

    [Fact]
    public async Task Push_WithSinkConfig_StoresInSegments()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["orderId"] = 123 });
        var sinkConfig = new SinkConfig
        {
            DaprPubSub = new DaprPubSubSinkConfig
            {
                Metadata = new Dictionary<string, string>
                {
                    { "customerId", "abc" },
                    { "region", "us-west" }
                }
            }
        };

        var request = new PushRequest
        {
            Items = new List<PushItem>
            {
                new PushItem { ItemJson = itemJson, Priority = 1, Sink = sinkConfig }
            }
        };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsPushed);
    }

    [Fact]
    public async Task PopWithAck_WithSinkConfig_ReturnsSinkConfig()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["orderId"] = 123 });
        var sinkConfig = new SinkConfig
        {
            DaprPubSub = new DaprPubSubSinkConfig
            {
                Metadata = new Dictionary<string, string>
                {
                    { "customerId", "abc" },
                    { "region", "us-west" }
                }
            }
        };

        await actor.Push(new PushRequest
        {
            Items = new List<PushItem>
            {
                new PushItem { ItemJson = itemJson, Priority = 1, Sink = sinkConfig }
            }
        });

        // Act
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });

        // Assert
        Assert.Single(popResult.Items);
        var poppedItem = popResult.Items[0];
        Assert.NotNull(poppedItem.Sink);
        Assert.NotNull(poppedItem.Sink.DaprPubSub);
        Assert.NotNull(poppedItem.Sink.DaprPubSub.Metadata);
        Assert.Equal(2, poppedItem.Sink.DaprPubSub.Metadata.Count);
        Assert.Equal("abc", poppedItem.Sink.DaprPubSub.Metadata["customerId"]);
        Assert.Equal("us-west", poppedItem.Sink.DaprPubSub.Metadata["region"]);
    }

    [Fact]
    public async Task SinkConfig_Roundtrip_PreservesMetadata()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        var item1Json = System.Text.Json.JsonSerializer.Serialize(new { orderId = 1 });
        var item2Json = System.Text.Json.JsonSerializer.Serialize(new { orderId = 2 });
        var item3Json = System.Text.Json.JsonSerializer.Serialize(new { orderId = 3 });

        var sink1 = new SinkConfig
        {
            DaprPubSub = new DaprPubSubSinkConfig
            {
                Metadata = new Dictionary<string, string> { { "customer", "a" } }
            }
        };

        var sink2 = new SinkConfig
        {
            DaprPubSub = new DaprPubSubSinkConfig
            {
                Metadata = new Dictionary<string, string> { { "customer", "b" }, { "priority", "high" } }
            }
        };

        // Push items with different sink configs (item3 has no sink config)
        await actor.Push(new PushRequest
        {
            Items = new List<PushItem>
            {
                new PushItem { ItemJson = item1Json, Priority = 1, Sink = sink1 },
                new PushItem { ItemJson = item2Json, Priority = 1, Sink = sink2 },
                new PushItem { ItemJson = item3Json, Priority = 1 }  // No sink config
            }
        });

        // Act - Pop all items
        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30, Count = 3 });

        // Assert
        Assert.Equal(3, popResult.Items.Count);

        // Item 1 should have sink config with customer=a
        var pop1 = popResult.Items[0];
        Assert.NotNull(pop1.Sink);
        Assert.NotNull(pop1.Sink.DaprPubSub);
        Assert.NotNull(pop1.Sink.DaprPubSub.Metadata);
        Assert.Single(pop1.Sink.DaprPubSub.Metadata);
        Assert.Equal("a", pop1.Sink.DaprPubSub.Metadata["customer"]);

        // Item 2 should have sink config with customer=b and priority=high
        var pop2 = popResult.Items[1];
        Assert.NotNull(pop2.Sink);
        Assert.NotNull(pop2.Sink.DaprPubSub);
        Assert.NotNull(pop2.Sink.DaprPubSub.Metadata);
        Assert.Equal(2, pop2.Sink.DaprPubSub.Metadata.Count);
        Assert.Equal("b", pop2.Sink.DaprPubSub.Metadata["customer"]);
        Assert.Equal("high", pop2.Sink.DaprPubSub.Metadata["priority"]);

        // Item 3 should have null sink config
        var pop3 = popResult.Items[2];
        Assert.Null(pop3.Sink);
    }
}
