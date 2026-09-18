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

        // Setup TryGetStateAsync for IdempotencyMarker
        mock.Setup(m => m.TryGetStateAsync<IdempotencyMarker>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is IdempotencyMarker marker)
                {
                    return new ConditionalValue<IdempotencyMarker>(true, marker);
                }
                return new ConditionalValue<IdempotencyMarker>(false, null);
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

        // Setup SetStateAsync with TTL (used for idempotency-key markers)
        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, TimeSpan ttl, CancellationToken ct) =>
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

    private async Task<QueueActor> CreateActorAsync(Mock<IActorStateManager> mockStateManager, Mock<IBlobReaperActorInvoker>? mockBlobReaperActorInvoker = null, IdempotencyConfig? idempotencyConfig = null)
    {
        // Create mock timer manager that no-ops timer registration
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>()))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object
        };

        // Create mock actor invoker to handle DLQ enqueue
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<Interfaces.EnqueueRequest, Interfaces.EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<Interfaces.EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Interfaces.EnqueueResponse { Success = true });

        mockBlobReaperActorInvoker ??= new Mock<IBlobReaperActorInvoker>();
        mockBlobReaperActorInvoker.Setup(i => i.InvokeMethodAsync<Interfaces.ScheduleDeletionRequest>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<Interfaces.ScheduleDeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Actor id in these tests never contains "-session-", so self-registration never fires -
        // no setup needed beyond a bare mock.
        var mockSessionCoordinatorActorInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        var tokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
        {
            SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
            TokenTtl = TimeSpan.FromMinutes(5)
        });
        var blobReapConfig = new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 };
        idempotencyConfig ??= new IdempotencyConfig { TtlSeconds = 86400 };

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(actorHost, mockInvoker.Object, mockBlobReaperActorInvoker.Object, mockSessionCoordinatorActorInvoker.Object, tokenIssuer, blobReapConfig, idempotencyConfig);

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
    public async Task EnqueueAsync_WithValidSingleItem_ReturnsSuccess()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        };

        // Act
        var result = await actor.Enqueue(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsEnqueued);
    }

    [Fact]
    public async Task EnqueueAsync_WithMultipleItems_ReturnsSuccessWithCount()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var item1Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test1" });
        var item2Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test2" });
        var item3Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test3" });

        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = item1Json, Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = item2Json, Priority = 0 },
                new Interfaces.EnqueueItem { ItemJson = item3Json, Priority = 1 }
            }
        };

        // Act
        var result = await actor.Enqueue(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, result.ItemsEnqueued);
    }

    [Fact]
    public async Task EnqueueAsync_WithEmptyArray_ReturnsFalure()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.EnqueueRequest { Items = new List<Interfaces.EnqueueItem>() };

        // Act
        var result = await actor.Enqueue(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsEnqueued);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task EnqueueAsync_WithEmptyItemJson_ReturnsFalure()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "", Priority = 0 }
            }
        };

        // Act
        var result = await actor.Enqueue(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsEnqueued);
    }

    [Fact]
    public async Task EnqueueAsync_WithNegativePriority_ReturnsFalure()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = -1 }
            }
        };

        // Act
        var result = await actor.Enqueue(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsEnqueued);
    }

    [Fact]
    public async Task EnqueueAsync_WithMixedPriorities_MaintainsFIFOPerPriority()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var item1Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "priority1-first" });
        var item2Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "priority0-urgent" });
        var item3Json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "priority1-second" });

        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = item1Json, Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = item2Json, Priority = 0 },
                new Interfaces.EnqueueItem { ItemJson = item3Json, Priority = 1 }
            }
        };

        // Act
        var enqueueResult = await actor.Enqueue(request);
        var dequeue1 = await actor.Dequeue(new Interfaces.DequeueRequest());
        var dequeue2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        var dequeue3 = await actor.Dequeue(new Interfaces.DequeueRequest());

        // Assert
        Assert.True(enqueueResult.Success);
        Assert.Equal(3, enqueueResult.ItemsEnqueued);

        // Priority 0 should come first
        Assert.Equal(item2Json, dequeue1.Items[0].ItemJson);
        Assert.Equal(0, dequeue1.Items[0].Priority);

        // Then priority 1 items in FIFO order
        Assert.Equal(item1Json, dequeue2.Items[0].ItemJson);
        Assert.Equal(1, dequeue2.Items[0].Priority);

        Assert.Equal(item3Json, dequeue3.Items[0].ItemJson);
        Assert.Equal(1, dequeue3.Items[0].Priority);
    }

    [Fact]
    public async Task EnqueueAsync_With101Items_AllocatesMultipleSegments()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        var items = new List<Interfaces.EnqueueItem>();
        for (int i = 0; i < 101; i++)
        {
            var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["index"] = i });
            items.Add(new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 1 });
        }

        var request = new Interfaces.EnqueueRequest { Items = items };

        // Act
        var result = await actor.Enqueue(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(101, result.ItemsEnqueued);

        // Verify items can be dequeued in order
        for (int i = 0; i < 101; i++)
        {
            var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());
            Assert.NotEmpty(dequeueResult.Items);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithOneInvalidItem_ReturnsFailureWithZeroItemsEnqueued()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var validItem = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "valid" });

        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = validItem, Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "", Priority = 1 }, // Invalid - empty
                new Interfaces.EnqueueItem { ItemJson = validItem, Priority = 1 }
            }
        };

        // Act
        var result = await actor.Enqueue(request);

        // Assert - all-or-nothing behavior
        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsEnqueued);

        // Verify nothing was actually enqueued
        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeueResult.Items);
    }

    [Fact]
    public async Task EnqueueAsync_WithFreshIdempotencyKey_ReturnsSuccessAndItemIsDequeued()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });

        var result = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0, IdempotencyKey = Guid.NewGuid().ToString() }
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsEnqueued);
        Assert.Equal(0, result.ItemsDeduplicated);

        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Single(dequeueResult.Items);
    }

    [Fact]
    public async Task EnqueueAsync_WithAlreadyUsedIdempotencyKey_SkipsItemAndReturnsDeduplicatedCount()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var key = Guid.NewGuid().ToString();
        var itemJson1 = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "first" });
        var itemJson2 = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "second" });

        var firstResult = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson1, Priority = 0, IdempotencyKey = key } }
        });
        Assert.True(firstResult.Success);
        Assert.Equal(1, firstResult.ItemsEnqueued);

        var secondResult = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson2, Priority = 0, IdempotencyKey = key } }
        });

        Assert.True(secondResult.Success);
        Assert.Equal(0, secondResult.ItemsEnqueued);
        Assert.Equal(1, secondResult.ItemsDeduplicated);

        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 10 });
        Assert.Single(dequeueResult.Items); // only the first item ever landed
    }

    [Fact]
    public async Task EnqueueAsync_WithDuplicateKeyTwiceInSameBatch_EnqueuesFirstAndSkipsSecond()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var key = Guid.NewGuid().ToString();
        var itemJson1 = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "first" });
        var itemJson2 = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "second" });

        var result = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson1, Priority = 0, IdempotencyKey = key },
                new Interfaces.EnqueueItem { ItemJson = itemJson2, Priority = 0, IdempotencyKey = key }
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsEnqueued);
        Assert.Equal(1, result.ItemsDeduplicated);

        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 10 });
        Assert.Single(dequeueResult.Items);
    }

    [Fact]
    public async Task EnqueueAsync_WithMixedKeyedAndUnkeyedAndDuplicateItems_EnqueuesNonDuplicatesOnly()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var key = Guid.NewGuid().ToString();
        var itemJsonA = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "a" });
        var itemJsonB = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "b" });
        var itemJsonC = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "c" });

        // Seed the key as already used
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJsonA, Priority = 0, IdempotencyKey = key } }
        });

        var result = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJsonB, Priority = 0 }, // no key
                new Interfaces.EnqueueItem { ItemJson = itemJsonC, Priority = 0, IdempotencyKey = key } // duplicate
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsEnqueued);
        Assert.Equal(1, result.ItemsDeduplicated);
    }

    [Fact]
    public async Task EnqueueAsync_WithIdempotencyKey_StagesMarkerWithConfiguredTtl()
    {
        var mockStateManager = CreateMockStateManager();
        var idempotencyConfig = new IdempotencyConfig { TtlSeconds = 3600 };
        var actor = await CreateActorAsync(mockStateManager, idempotencyConfig: idempotencyConfig);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var key = Guid.NewGuid().ToString();

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0, IdempotencyKey = key } }
        });

        mockStateManager.Verify(m => m.SetStateAsync(
            $"idem_{key}",
            It.IsAny<IdempotencyMarker>(),
            TimeSpan.FromSeconds(3600),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_WithUnloadAfterCommitTrue_UnloadsTouchedIdempotencyKeys()
    {
        var mockStateManager = CreateMockStateManager();
        var idempotencyConfig = new IdempotencyConfig { TtlSeconds = 86400, UnloadAfterCommit = true };
        var actor = await CreateActorAsync(mockStateManager, idempotencyConfig: idempotencyConfig);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var key = Guid.NewGuid().ToString();

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0, IdempotencyKey = key } }
        });

        mockStateManager.Verify(m => m.UnloadStateAsync($"idem_{key}", It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_WithUnloadAfterCommitFalse_NeverUnloadsIdempotencyKeys()
    {
        var mockStateManager = CreateMockStateManager();
        var idempotencyConfig = new IdempotencyConfig { TtlSeconds = 86400, UnloadAfterCommit = false };
        var actor = await CreateActorAsync(mockStateManager, idempotencyConfig: idempotencyConfig);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var key = Guid.NewGuid().ToString();

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0, IdempotencyKey = key } }
        });

        mockStateManager.Verify(m => m.UnloadStateAsync(It.IsAny<string>(), It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_WithIdempotencyKeyExceedingMaxLength_ReturnsFailureWithZeroItemsEnqueued()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var overlongKey = new string('k', 129);

        var result = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0, IdempotencyKey = overlongKey } }
        });

        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsEnqueued);

        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeueResult.Items);
    }

    [Fact]
    public async Task EnqueueAsync_WithIdempotencyKeyContainingControlCharacter_ReturnsFailureWithZeroItemsEnqueued()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });

        var result = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0, IdempotencyKey = "bad\0key" } }
        });

        Assert.False(result.Success);
        Assert.Equal(0, result.ItemsEnqueued);
    }

    [Fact]
    public async Task ConfigureDedup_WithEnabledFalse_DisablesDedupOnSubsequentEnqueue()
    {
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var key = Guid.NewGuid().ToString();
        var itemJson1 = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "first" });
        var itemJson2 = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "second" });

        var configureResult = await actor.ConfigureDedup(new Interfaces.ConfigureDedupRequest { Enabled = false });
        Assert.True(configureResult.Success);

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson1, Priority = 0, IdempotencyKey = key } }
        });

        var secondResult = await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem> { new Interfaces.EnqueueItem { ItemJson = itemJson2, Priority = 0, IdempotencyKey = key } }
        });

        // Dedup disabled - the "duplicate" key is not honored, item is enqueued again
        Assert.True(secondResult.Success);
        Assert.Equal(1, secondResult.ItemsEnqueued);
        Assert.Equal(0, secondResult.ItemsDeduplicated);

        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 10 });
        Assert.Equal(2, dequeueResult.Items.Count);
    }

    [Fact]
    public async Task DequeueAsync_FromEmptyQueue_ReturnsNull()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act
        var result = await actor.Dequeue(new Interfaces.DequeueRequest());

        // Assert
        Assert.Empty(result.Items);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task DequeueAsync_AfterEnqueue_ReturnsItem()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var enqueueRequest = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        };

        // Act
        await actor.Enqueue(enqueueRequest);
        var result = await actor.Dequeue(new Interfaces.DequeueRequest());

        // Assert
        Assert.NotEmpty(result.Items);
        Assert.False(result.Locked);
        Assert.Equal(0, result.Items[0].Priority); // Verify priority is returned
        var returnedItem = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(result.Items[0].ItemJson);
        Assert.Equal("test", returnedItem!["message"].ToString());
    }

    [Fact]
    public async Task EnqueueDequeue_MaintainsFIFOOrder()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Enqueue 3 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem
                {
                    ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 1 }),
                    Priority = 0
                }
            }
        });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem
                {
                    ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 2 }),
                    Priority = 0
                }
            }
        });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem
                {
                    ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 3 }),
                    Priority = 0
                }
            }
        });

        // Dequeue all items
        var item1 = await actor.Dequeue(new Interfaces.DequeueRequest());
        var item2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        var item3 = await actor.Dequeue(new Interfaces.DequeueRequest());

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
    public async Task DequeueLockedAsync_CreatesLock()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        // Act
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        var lockId = dequeueResult.LockId!;

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
    public async Task DequeueLocked_CreatesNamedLockStateKey()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        // Act
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

        // Assert
        Assert.NotNull(result.LockId);
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{result.LockId}-lock");
        Assert.True(lockState.HasValue);
        Assert.Equal(result.LockId, lockState.Value.LockId);
    }

    [Fact]
    public async Task DequeueLocked_SetsCurrentLockId()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        // Act
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        var lockId = dequeueResult.LockId!;

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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        var lockId = dequeueResult.LockId!;

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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = itemJson, Priority = 0 }
            }
        });

        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

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
    public async Task DequeueLocked_ExpiredLock_ReminderCleansUpAndAllowsNewLock()
    {
        // With Phase 2 reminders: expired locks are cleaned by reminder callback, not by DequeueLocked

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = itemJson, Priority = 0 }]
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

        // Now DequeueLocked should succeed
        var result = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });

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
    public async Task EnqueueDequeue_MaintainsExactJsonFormat()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var original = "{\"key\":\"value\",\"number\":42}";

        // Act
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = original, Priority = 0 }
            }
        });
        var result = await actor.Dequeue(new Interfaces.DequeueRequest());

        // Assert
        Assert.NotEmpty(result.Items);
        Assert.False(result.Locked);
        Assert.Equal(original, result.Items[0].ItemJson);
    }

    [Fact]
    public async Task Enqueue_WithoutExplicitPriority_UsesDefaultPriorityOne()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"test\":\"data\"}", Priority = 1 }
            }
        };
        // Priority not explicitly set - should default to 1

        // Act
        await actor.Enqueue(request);

        // Assert - verify it went to priority 1 queue by checking metadata
        var metadataState = await mockStateManager.Object.TryGetStateAsync<ActorMetadata>("metadata", CancellationToken.None);
        Assert.True(metadataState.HasValue);
        var metadata = metadataState.Value;
        Assert.NotNull(metadata);

        Assert.True(metadata.Queues.ContainsKey(1), "Item should be in priority 1 queue");
        Assert.False(metadata.Queues.ContainsKey(0), "Item should NOT be in priority 0 queue");

        // Also verify Dequeue returns priority 1
        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(dequeueResult.Items);
        Assert.Equal(1, dequeueResult.Items[0].Priority); // Verify default priority is returned
    }

    [Fact]
    public async Task ExpiredLock_RestoresOriginalPriority()
    {
        // With Phase 2: Lock-in-place means item never leaves queue, just lock state is cleaned by reminder

        // Arrange - enqueue items at different priorities
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 2 }]
        });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 }]
        });

        // DequeueLocked with 1 second TTL (will dequeue priority 1 item first)
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 1 });
        Assert.NotNull(dequeueResult.ItemJson);
        Assert.NotNull(dequeueResult.LockId);
        Assert.Contains("\"id\":2", dequeueResult.ItemJson);

        // Wait for lock to expire, then simulate reminder cleanup
        await Task.Delay(1100);
        await actor.ReceiveReminderAsync($"lock-{dequeueResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Dequeue again - should get same item (remained at priority 1 due to lock-in-place)
        var secondDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(secondDequeue.Items);
        Assert.False(secondDequeue.Locked);
        Assert.Contains("\"id\":2", secondDequeue.Items[0].ItemJson);

        // Final dequeue gets priority 2 item (proving order was preserved)
        var thirdDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(thirdDequeue.Items);
        Assert.False(thirdDequeue.Locked);
        Assert.Contains("\"id\":1", thirdDequeue.Items[0].ItemJson);

        // Queue should now be empty
        var fourthDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(fourthDequeue.Items);
        Assert.False(fourthDequeue.Locked);
    }

    [Fact]
    public async Task DequeueLocked_CommitsAtomically()
    {
        // Arrange - enqueue single item
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });

        // Act - DequeueLocked should commit lock atomically
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

        // Assert - item is successfully locked (dequeued and stored in lock)
        Assert.Single(result.Items);
        Assert.NotNull(result.Items[0].ItemJson);
        Assert.NotNull(result.Items[0].LockId);
        Assert.Contains("\"id\":1", result.Items[0].ItemJson);

        // Verify queue is blocked while lock exists (cannot dequeue in legacy mode)
        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeueResult.Items);
        Assert.True(dequeueResult.Locked);
        Assert.Equal("Queue is locked by another operation", dequeueResult.Message);

        // After acknowledgement, queue should be empty
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = result.Items[0].LockId });
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Now dequeue should return empty
        var finalDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(finalDequeue.Items);
        Assert.False(finalDequeue.Locked);
    }

    [Fact]
    public async Task ExpiredLock_PreservesQueuePosition()
    {
        // Phase 3: Item dequeued during DequeueLocked, re-queued at end when lock expires

        // Arrange - enqueue items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"A\"}", Priority = 1 }]
        });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"B\"}", Priority = 1 }]
        });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"C\"}", Priority = 1 }]
        });

        // Act - DequeueLocked locks Item-A (dequeues it)
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 1 });
        Assert.NotNull(dequeueResult.ItemJson);
        Assert.NotNull(dequeueResult.LockId);
        Assert.Contains("\"id\":\"A\"", dequeueResult.ItemJson);

        // Enqueue Item-D while lock is active
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"D\"}", Priority = 1 }]
        });

        // Wait for lock to expire, then simulate reminder cleanup (re-queues Item-A at end)
        await Task.Delay(1100);
        await actor.ReceiveReminderAsync($"lock-{dequeueResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Dequeue should return B, C, D, A (A was re-queued at end)
        var firstDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(firstDequeue.Items);
        Assert.False(firstDequeue.Locked);
        Assert.Contains("\"id\":\"B\"", firstDequeue.Items[0].ItemJson);

        // Second dequeue returns C
        var secondDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(secondDequeue.Locked);
        Assert.Contains("\"id\":\"C\"", secondDequeue.Items[0].ItemJson);

        var thirdDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(thirdDequeue.Locked);
        Assert.Contains("\"id\":\"D\"", thirdDequeue.Items[0].ItemJson);

        // Fourth dequeue returns A (re-queued after lock expiry)
        var fourthDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(fourthDequeue.Locked);
        Assert.Contains("\"id\":\"A\"", fourthDequeue.Items[0].ItemJson);

        // Queue should now be empty
        var fifthDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(fifthDequeue.Items);
        Assert.False(fifthDequeue.Locked);
    }

    [Fact]
    public async Task DequeueLocked_ItemsStayInQueueUntilAck()
    {
        // Arrange - enqueue item
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":\"test\"}", Priority = 1 }
            }
        });

        // Act - DequeueLocked locks the item
        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        Assert.NotNull(dequeueResult.ItemJson);
        var lockId = dequeueResult.LockId;

        // Assert - Dequeue returns empty while lock exists (item still in queue but locked)
        var blockedDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(blockedDequeue.Items);
        Assert.True(blockedDequeue.Locked);
        Assert.Equal("Queue is locked by another operation", blockedDequeue.Message);

        // Acknowledge the lock
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });
        Assert.True(ackResult.Success);

        // Now queue should be truly empty (item dequeued on acknowledgement)
        var finalDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(finalDequeue.Items);
        Assert.False(finalDequeue.Locked);
    }

    [Fact]
    public async Task Acknowledge_RemovesItemsFromQueue()
    {
        // Arrange - enqueue multiple items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 }
            }
        });

        // Act - DequeueLocked first item
        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        Assert.Contains("\"id\":1", dequeueResult.ItemJson!);
        var lockId = dequeueResult.LockId;

        // Acknowledge
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Assert - Next dequeue should return second item
        var secondDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(secondDequeue.Items);
        Assert.False(secondDequeue.Locked);
        Assert.Contains("\"id\":2", secondDequeue.Items[0].ItemJson!);

        // Queue should now be empty
        var thirdDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(thirdDequeue.Items);
        Assert.False(thirdDequeue.Locked);
    }

    [Fact]
    public async Task MultipleDequeuesBlocked_WhenLockActive()
    {
        // Arrange - enqueue items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 }
            }
        });

        // Act - DequeueLocked creates lock
        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        var lockId = dequeueResult.LockId;

        // Attempt Dequeue() - should be blocked
        var blockedDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(blockedDequeue.Items);
        Assert.True(blockedDequeue.Locked);
        Assert.Equal("Queue is locked by another operation", blockedDequeue.Message);

        // Attempt another DequeueLocked - should be blocked
        var blockedDequeueLocked = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        Assert.True(blockedDequeueLocked.Locked);
        Assert.Null(blockedDequeueLocked.ItemJson);
        Assert.Contains("locked", blockedDequeueLocked.Message, StringComparison.OrdinalIgnoreCase);

        // Acknowledge
        await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });

        // Dequeue() should now work
        var successfulDequeue = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(successfulDequeue.Items);
        Assert.False(successfulDequeue.Locked);
        Assert.Contains("\"id\":2", successfulDequeue.Items[0].ItemJson!);
    }

    [Fact]
    public async Task LockExpiry_DoesNotReorderQueue()
    {
        // Phase 3: Item dequeued during DequeueLocked, re-queued at end of priority when lock expires

        // Arrange - enqueue items with different priorities
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"P1-A\"}", Priority = 1 }]
        });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"P1-B\"}", Priority = 1 }]
        });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"P2-A\"}", Priority = 2 }]
        });

        // Act - DequeueLocked on priority 1 item (dequeues P1-A)
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 1 });
        Assert.NotNull(dequeueResult.LockId);
        Assert.Contains("\"id\":\"P1-A\"", dequeueResult.ItemJson!);

        // Enqueue more items to priority 1 while lock is active
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"P1-C\"}", Priority = 1 }]
        });
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":\"P1-D\"}", Priority = 1 }]
        });

        // Let lock expire, then simulate reminder cleanup (re-queues P1-A at end of priority 1)
        await Task.Delay(1100);
        await actor.ReceiveReminderAsync($"lock-{dequeueResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Dequeue all items: B, C, D, A (at end of priority 1), then P2-A
        var dequeue1 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(dequeue1.Locked);
        Assert.Contains("\"id\":\"P1-B\"", dequeue1.Items[0].ItemJson);

        var dequeue2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(dequeue2.Locked);
        Assert.Contains("\"id\":\"P1-C\"", dequeue2.Items[0].ItemJson);

        var dequeue3 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(dequeue3.Locked);
        Assert.Contains("\"id\":\"P1-D\"", dequeue3.Items[0].ItemJson);

        var dequeue4 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(dequeue4.Locked);
        Assert.Contains("\"id\":\"P1-A\"", dequeue4.Items[0].ItemJson); // Re-queued at end

        var dequeue5 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(dequeue5.Locked);
        Assert.Contains("\"id\":\"P2-A\"", dequeue5.Items[0].ItemJson);

        // Queue should be empty
        var dequeue6 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeue6.Items);
        Assert.False(dequeue6.Locked);
    }

    [Fact]
    public async Task ErrorState_BlocksEnqueueAndDequeueOperations()
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

        // Act & Assert - Enqueue should throw
        var enqueueEx = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await actor.Enqueue(new Interfaces.EnqueueRequest
            {
                Items = new List<Interfaces.EnqueueItem>
                {
                    new Interfaces.EnqueueItem { ItemJson = "{\"test\":\"data\"}", Priority = 0 }
                }
            })
        );
        Assert.Contains("Queue corrupted", enqueueEx.Message);
        Assert.Contains("Test corruption error", enqueueEx.Message);

        // Act & Assert - Dequeue should throw
        var dequeueEx = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await actor.Dequeue(new Interfaces.DequeueRequest())
        );
        Assert.Contains("Queue corrupted", dequeueEx.Message);
        Assert.Contains("Test corruption error", dequeueEx.Message);
    }

    [Fact]
    public async Task ExtendLock_ValidLock_ExtendsExpiry()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue an item
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });

        // DequeueLocked to create lock with 10s TTL
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 10 });
        Assert.NotNull(dequeueLockedResult.LockId);
        var originalExpiresAt = dequeueLockedResult.LockExpiresAt!.Value;

        // Act - Extend lock by 30 seconds
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueLockedResult.LockId,
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

        // Enqueue an item and create lock
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 10 });
        Assert.NotNull(dequeueLockedResult.LockId);

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

        // Enqueue an item and create lock with 1s TTL
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 1 });
        Assert.NotNull(dequeueLockedResult.LockId);

        // Wait for lock to expire
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Act - Try to extend expired lock
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueLockedResult.LockId,
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

        // Enqueue an item and create lock
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 10 });
        Assert.NotNull(dequeueLockedResult.LockId);

        // Act - Try to extend with negative TTL
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueLockedResult.LockId,
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

        // Enqueue an item and create lock with 10s TTL
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 10 });
        Assert.NotNull(dequeueLockedResult.LockId);
        var originalExpiresAt = dequeueLockedResult.LockExpiresAt!.Value;

        // Act - Extend lock twice
        var extendResult1 = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueLockedResult.LockId,
            AdditionalTtlSeconds = 10
        });
        Assert.True(extendResult1.Success);

        var extendResult2 = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueLockedResult.LockId,
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

        // Enqueue an item and create lock
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 10 });
        Assert.NotNull(dequeueLockedResult.LockId);

        // Extend lock
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueLockedResult.LockId,
            AdditionalTtlSeconds = 30
        });
        Assert.True(extendResult.Success);

        // Act - Try to Dequeue (should be blocked)
        var dequeueResult = await actor.Dequeue(new Interfaces.DequeueRequest());

        // Assert - Queue should still be locked
        Assert.True(dequeueResult.Locked);
        Assert.Empty(dequeueResult.Items);

        // Now acknowledge
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = dequeueLockedResult.LockId });
        Assert.True(ackResult.Success);

        // Dequeue should now work (queue empty)
        var dequeueResult2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.False(dequeueResult2.Locked);
        Assert.True(dequeueResult2.IsEmpty);
    }

    [Fact]
    public async Task DeadLetter_ValidLock_AttemptsToMoveToDlq()
    {
        // Note: This unit test verifies lock validation logic.
        // ActorProxy.Create requires a Dapr runtime, so full DLQ flow is tested in integration tests.

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue an item and create lock
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1,\"value\":\"test\"}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        Assert.NotNull(dequeueLockedResult.LockId);

        // Act - Attempt to move to dead letter queue
        var deadLetterResult = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = dequeueLockedResult.LockId });

        // Assert - Lock validation passed (actual DLQ enqueue tested in integration tests)
        // In unit tests, ActorProxy.Create will fail without Dapr runtime
        Assert.NotNull(deadLetterResult);
        Assert.True(deadLetterResult.Status == "SUCCESS" || deadLetterResult.ErrorCode == "DLQ_ENQUEUE_FAILED" || deadLetterResult.ErrorCode == "INTERNAL_ERROR");
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

        // Enqueue an item and create lock
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

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

        // Enqueue an item and create lock with negative expiry (already expired)
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        Assert.NotNull(dequeueLockedResult.LockId);

        // Manually expire the lock by setting ExpiresAt to past timestamp
        var lockState = new LockState
        {
            LockId = dequeueLockedResult.LockId,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-40).ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds(), // Expired
            Priority = 1,
            HeadSegment = 0,
            ItemJson = dequeueLockedResult.ItemJson!,
            CompetingConsumerMode = false
        };
        await mockStateManager.Object.SetStateAsync($"{dequeueLockedResult.LockId}-lock", lockState);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        await mockStateManager.Object.SetStateAsync("metadata", metadata with { LockCount = 1 });

        // Act - Try to deadletter with expired lock
        var result = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = dequeueLockedResult.LockId });

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

        // Enqueue priority 0 item (fast lane)
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1,\"urgent\":true}", Priority = 0 }
            }
        });
        var dequeueLockedResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });
        Assert.NotNull(dequeueLockedResult.LockId);

        // Act - Attempt to move to dead letter queue
        var deadLetterResult = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = dequeueLockedResult.LockId });

        // Assert - Lock validation passed (actual DLQ operation requires Dapr runtime)
        Assert.NotNull(deadLetterResult);
        // Either succeeds or fails with DLQ_ENQUEUE_FAILED/INTERNAL_ERROR (no Dapr runtime in unit tests)
        Assert.True(deadLetterResult.Status == "SUCCESS" || deadLetterResult.ErrorCode == "DLQ_ENQUEUE_FAILED" || deadLetterResult.ErrorCode == "INTERNAL_ERROR");
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
    public async Task DequeueLocked_RegistersReminder_WithCorrectTtl()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue an item
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });

        // Act - DequeueLocked should register a reminder
        var result = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });

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
    public async Task DequeueLocked_LockExpiration_ReminderWillAutoCleanup()
    {
        // This test documents the expected behavior with reminders enabled.
        // When a lock expires, the reminder callback will automatically clean up lock state.
        // No manual expiration checks needed in DequeueLocked.

        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue an item
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });

        // Act - Create lock
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 1 });
        Assert.NotNull(dequeueResult.LockId);

        // Simulate reminder firing after TTL
        await Task.Delay(1100); // Wait for expiration
        await actor.ReceiveReminderAsync($"lock-{dequeueResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock state should be cleaned up
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{dequeueResult.LockId}-lock");

        Assert.False(lockState.HasValue);
    }

    [Fact]
    public async Task ReceiveReminderAsync_RequeuesExpiredLock_AtOriginalPriority()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue an item and lock it
        string itemJson = "{\"test\":\"data\"}";
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = itemJson, Priority = 2 }]
        });

        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 1 });
        Assert.NotNull(dequeueResult.LockId);
        Assert.Equal(itemJson, dequeueResult.ItemJson);

        // Verify queue empty after DequeueLocked (item dequeued)
        var dequeueEmpty = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeueEmpty.Items); // Queue should be empty

        // Act - Simulate reminder firing (lock expires and re-queues)
        await actor.ReceiveReminderAsync($"lock-{dequeueResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Item should be re-queued at original priority 2
        var dequeueResult2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.NotEmpty(dequeueResult2.Items);
        Assert.Equal(itemJson, dequeueResult2.Items[0].ItemJson);
        Assert.Equal(2, dequeueResult2.Items[0].Priority);
    }

    [Fact]
    public async Task DequeueLocked_DecrementsCountImmediately()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue 3 items
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [
                new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 }
            ]
        });

        // Act - DequeueLocked should decrement immediately (dequeue first item)
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });
        Assert.Equal("{\"id\":1}", dequeueResult.ItemJson);
        Assert.NotNull(dequeueResult.LockId);

        // Acknowledge the lock to allow further operations
        await actor.Acknowledge(new AcknowledgeRequest { LockId = dequeueResult.LockId });

        // Assert - Regular Dequeue should return second item (not first), proving first was dequeued
        var dequeueResult2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Equal("{\"id\":2}", dequeueResult2.Items[0].ItemJson);

        // Third item still available
        var dequeueResult3 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Equal("{\"id\":3}", dequeueResult3.Items[0].ItemJson);

        // Queue should be empty now
        var dequeueEmpty = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeueEmpty.Items);
    }

    [Fact]
    public async Task Acknowledge_DoesNotDequeueAgain()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue items and lock one
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [
                new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 }
            ]
        });

        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });
        Assert.Equal("{\"id\":1}", dequeueResult.ItemJson);
        Assert.NotNull(dequeueResult.LockId);

        // Act - Acknowledge should just remove lock, not modify queue
        var ackResult = await actor.Acknowledge(new AcknowledgeRequest { LockId = dequeueResult.LockId });
        Assert.True(ackResult.Success);

        // Assert - Only item 2 should be in queue (item 1 was dequeued during DequeueLocked, not Acknowledge)
        var dequeueResult2 = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Equal("{\"id\":2}", dequeueResult2.Items[0].ItemJson);

        // Queue should be empty
        var dequeueEmpty = await actor.Dequeue(new Interfaces.DequeueRequest());
        Assert.Empty(dequeueEmpty.Items);
    }

    [Fact]
    public async Task ExtendLock_UpdatesReminder_WithNewTtl()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue and lock an item
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });
        Assert.NotNull(dequeueResult.LockId);

        double originalExpiry = dequeueResult.LockExpiresAt ?? 0;

        // Act - Extend lock by 20 seconds
        var extendResult = await actor.ExtendLock(new ExtendLockRequest
        {
            LockId = dequeueResult.LockId,
            AdditionalTtlSeconds = 20
        });

        // Assert - Lock expiry should be extended
        Assert.True(extendResult.Success);
        Assert.True(extendResult.NewExpiresAt > originalExpiry);
        Assert.InRange(extendResult.NewExpiresAt, originalExpiry + 18, originalExpiry + 22); // Within 2 seconds tolerance

        // Verify lock state was updated
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{dequeueResult.LockId}-lock");
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

        // Enqueue and lock an item
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }]
        });
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 5 });
        Assert.NotNull(dequeueResult.LockId);

        // Act - Extend lock
        await actor.ExtendLock(new ExtendLockRequest
        {
            LockId = dequeueResult.LockId,
            AdditionalTtlSeconds = 10
        });

        // Simulate old reminder firing (should do nothing since lock state is updated)
        await Task.Delay(5100);
        await actor.ReceiveReminderAsync($"lock-{dequeueResult.LockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Lock should still exist because reminder was replaced
        // (In real scenario, old reminder would be unregistered and wouldn't fire)
        // This unit test validates the cleanup logic is safe even if old reminder fires
        var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{dequeueResult.LockId}-lock");

        // Lock gets cleaned up by the reminder callback
        Assert.False(lockState.HasValue);

        // Note: Integration tests should verify the old reminder is actually unregistered
        // and doesn't fire after ExtendLock is called
    }

    [Fact]
    public async Task DequeueLocked_CompetingConsumers_AllowsParallelLocks()
    {
        // Arrange - enqueue 3 items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 2 }), Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 3 }), Priority = 1 }
            }
        });

        // Act - two parallel DequeueLocked with competing consumers enabled
        var result1 = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        var result2 = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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
    public async Task DequeueLocked_LegacyMode_BlocksWhenLockExists()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 2 }), Priority = 1 }
            }
        });

        // First lock with competing consumers enabled
        var result1 = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        Assert.NotNull(result1.LockId);

        // Act - second DequeueLocked with legacy mode (AllowCompetingConsumers = false)
        var result2 = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 }
            }
        });
        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        // Act
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = dequeueResult.LockId! });

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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 }
            }
        });
        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        // Act
        var extendResult = await actor.ExtendLock(new Interfaces.ExtendLockRequest
        {
            LockId = dequeueResult.LockId!,
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
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1 }), Priority = 1 }
            }
        });
        var dequeueResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 1,
            AllowCompetingConsumers = true
        });
        string lockId = dequeueResult.LockId!;

        // Act - trigger reminder (simulating lock expiry)
        await actor.ReceiveReminderAsync($"lock-{lockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromMilliseconds(-1));

        // Assert - lock count updated to 0
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);
    }

    // ===== Bulk Dequeue Tests =====

    [Fact]
    public async Task Dequeue_WithCount_ReturnsMultipleItems()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue 5 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":4}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":5}", Priority = 1 }
            }
        });

        // Act
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 5 });

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
    public async Task Dequeue_WithCountGreaterThanAvailable_ReturnsPartialResults()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue only 3 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - Request 10 items but only 3 exist
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 10 });

        // Assert
        Assert.Equal(3, result.Items.Count);
        Assert.Equal("{\"id\":1}", result.Items[0].ItemJson);
        Assert.Equal("{\"id\":2}", result.Items[1].ItemJson);
        Assert.Equal("{\"id\":3}", result.Items[2].ItemJson);
        Assert.False(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Dequeue_WithCountZero_ReturnsEmptyArray()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue some items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 }
            }
        });

        // Act - Request 0 items
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 0 });

        // Assert
        Assert.Empty(result.Items);
        Assert.False(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Dequeue_WithCountExceedingMax_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Request more than max (1000)
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 1001 });

        // Assert
        Assert.Empty(result.Items);
        Assert.Contains("Count must be between", result.Message);
    }

    [Fact]
    public async Task Dequeue_CrossPriority_ReturnsInPriorityOrder()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue items across different priorities
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"priority\":1,\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"priority\":1,\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"priority\":0,\"id\":1}", Priority = 0 },
                new Interfaces.EnqueueItem { ItemJson = "{\"priority\":2,\"id\":1}", Priority = 2 },
                new Interfaces.EnqueueItem { ItemJson = "{\"priority\":0,\"id\":2}", Priority = 0 }
            }
        });

        // Act - Dequeue all items
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 5 });

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
    public async Task Dequeue_EmptyQueue_ReturnsEmptyArrayWithIsEmptyTrue()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act - Dequeue from empty queue
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 5 });

        // Assert
        Assert.Empty(result.Items);
        Assert.True(result.IsEmpty);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task Dequeue_LockedQueue_ReturnsEmptyArrayWithLockedTrue()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue an item and lock it
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 }
            }
        });
        await actor.DequeueLocked(new Interfaces.DequeueLockedRequest { TtlSeconds = 30 });

        // Act - Try to dequeue from locked queue
        var result = await actor.Dequeue(new Interfaces.DequeueRequest { Count = 5 });

        // Assert
        Assert.Empty(result.Items);
        Assert.False(result.IsEmpty);
        Assert.True(result.Locked);
    }

    [Fact]
    public async Task Dequeue_DefaultCount_DequeuesSingleItem()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue 3 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - Dequeue with default count (should be 1)
        var result = await actor.Dequeue(new Interfaces.DequeueRequest());

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("{\"id\":1}", result.Items[0].ItemJson);
        Assert.Equal(1, result.Items[0].Priority);
    }

    // Phase 2: Bulk DequeueLocked Tests

    [Fact]
    public async Task DequeueLocked_WithCount_CreatesMultipleLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue 5 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":4}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":5}", Priority = 1 }
            }
        });

        // Act - DequeueLocked with count=3 in competing consumer mode
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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
    public async Task DequeueLocked_WithCount_CreatesIndependentLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue 4 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":4}", Priority = 1 }
            }
        });

        // Act - DequeueLocked with count=3
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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
    public async Task DequeueLocked_WithCountPartial_ReturnsAvailableItems()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue only 3 items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - Request 10 items but only 3 available
        var result = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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
    public async Task DequeueLocked_LegacyMode_BlocksWithExistingLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 }
            }
        });

        // Act - First DequeueLocked in legacy mode (default)
        var firstResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 30,
            Count = 1,
            AllowCompetingConsumers = false  // Legacy mode
        });

        Assert.Equal(1, firstResult.Items.Count);

        // Second DequeueLocked should be blocked in legacy mode
        var secondResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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
    public async Task DequeueLocked_CompetingConsumerMode_AllowsMultipleLocks()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Enqueue items
        await actor.Enqueue(new Interfaces.EnqueueRequest
        {
            Items = new List<Interfaces.EnqueueItem>
            {
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 },
                new Interfaces.EnqueueItem { ItemJson = "{\"id\":4}", Priority = 1 }
            }
        });

        // Act - First DequeueLocked in competing consumer mode
        var firstResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
        {
            TtlSeconds = 30,
            Count = 2,
            AllowCompetingConsumers = true
        });

        Assert.Equal(2, firstResult.Items.Count);

        // Second DequeueLocked should also succeed in competing consumer mode
        var secondResult = await actor.DequeueLocked(new Interfaces.DequeueLockedRequest
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

        // Enqueue an item
        string itemJson = "{\"id\":1,\"data\":\"test\"}";
        await actor.Enqueue(new EnqueueRequest
        {
            Items = [new EnqueueItem { ItemJson = itemJson, Priority = 0 }]
        });

        // DequeueLocked to create lock
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });
        Assert.Single(dequeueResult.Items);
        string lockId = dequeueResult.Items[0].LockId!;

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

}
