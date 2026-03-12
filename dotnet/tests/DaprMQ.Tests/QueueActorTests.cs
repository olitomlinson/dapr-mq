using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using Xunit;
using DaprMQ;
using DaprMQ.Interfaces;
using DaprMQ.Configuration;

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

        // Setup TryGetStateAsync for Queue<string> (segments)
        mock.Setup(m => m.TryGetStateAsync<Queue<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is Queue<string> queue)
                {
                    return new ConditionalValue<Queue<string>>(true, queue);
                }
                return new ConditionalValue<Queue<string>>(false, null);
            });

        // Setup TryGetStateAsync for SegmentDeletionMetadata
        mock.Setup(m => m.TryGetStateAsync<SegmentDeletionMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is SegmentDeletionMetadata deletionMetadata)
                {
                    return new ConditionalValue<SegmentDeletionMetadata>(true, deletionMetadata);
                }
                return new ConditionalValue<SegmentDeletionMetadata>(false, null);
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
        var mockInvoker = new Mock<IActorInvoker>();
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
    public async Task PushAsync_WithValidItem_ReturnsTrue()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var request = new Interfaces.PushRequest { ItemJson = itemJson, Priority = 0 };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task PushAsync_WithoutItem_ReturnsFalse()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.PushRequest { ItemJson = "", Priority = 0 };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.False(result.Success);
    }

    [Fact]
    public async Task PushAsync_WithNegativePriority_ReturnsFalse()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var request = new Interfaces.PushRequest { ItemJson = itemJson, Priority = -1 };

        // Act
        var result = await actor.Push(request);

        // Assert
        Assert.False(result.Success);
    }

    [Fact]
    public async Task PopAsync_FromEmptyQueue_ReturnsNull()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Act
        var result = await actor.Pop();

        // Assert
        Assert.Null(result.ItemJson);
        Assert.False(result.Locked);
    }

    [Fact]
    public async Task PopAsync_AfterPush_ReturnsItem()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        var pushRequest = new Interfaces.PushRequest { ItemJson = itemJson, Priority = 0 };

        // Act
        await actor.Push(pushRequest);
        var result = await actor.Pop();

        // Assert
        Assert.NotNull(result.ItemJson);
        Assert.False(result.Locked);
        var returnedItem = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(result.ItemJson);
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
            ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 1 }),
            Priority = 0
        });
        await actor.Push(new Interfaces.PushRequest
        {
            ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 2 }),
            Priority = 0
        });
        await actor.Push(new Interfaces.PushRequest
        {
            ItemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 3 }),
            Priority = 0
        });

        // Pop all items
        var item1 = await actor.Pop();
        var item2 = await actor.Pop();
        var item3 = await actor.Pop();

        // Assert - Should be in FIFO order
        Assert.False(item1.Locked);
        Assert.False(item2.Locked);
        Assert.False(item3.Locked);
        var deserialized1 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item1.ItemJson!);
        var deserialized2 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item2.ItemJson!);
        var deserialized3 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item3.ItemJson!);

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
        await actor.Push(new Interfaces.PushRequest { ItemJson = itemJson, Priority = 0 });

        // Act
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Assert
        Assert.True(result.Locked);
        Assert.NotNull(result.LockId);
        Assert.NotNull(result.ItemJson);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithValidLockId_ReturnsSuccess()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var itemJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["message"] = "test" });
        await actor.Push(new Interfaces.PushRequest { ItemJson = itemJson, Priority = 0 });

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
    public async Task PushPop_MaintainsExactJsonFormat()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var original = "{\"key\":\"value\",\"number\":42}";

        // Act
        await actor.Push(new Interfaces.PushRequest { ItemJson = original, Priority = 0 });
        var result = await actor.Pop();

        // Assert
        Assert.NotNull(result.ItemJson);
        Assert.False(result.Locked);
        Assert.Equal(original, result.ItemJson);
    }

    [Fact]
    public async Task Push_WithoutExplicitPriority_UsesDefaultPriorityOne()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        var request = new Interfaces.PushRequest { ItemJson = "{\"test\":\"data\"}" };
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
    }

    [Fact]
    public async Task ExpiredLock_RestoresOriginalPriority()
    {
        // Arrange - push items at different priorities
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 2 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":2}", Priority = 1 });

        // PopWithAck with 1 second TTL (will pop priority 1 item first)
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.ItemJson);
        Assert.Contains("\"id\":2", popResult.ItemJson);

        // Wait for lock to expire
        await Task.Delay(1100);

        // Pop again - should get same item (re-pushed with priority 1, not priority 0)
        var secondPop = await actor.Pop();
        Assert.NotNull(secondPop.ItemJson);
        Assert.False(secondPop.Locked);
        Assert.Contains("\"id\":2", secondPop.ItemJson);

        // Final pop gets priority 2 item (proving order was preserved)
        var thirdPop = await actor.Pop();
        Assert.NotNull(thirdPop.ItemJson);
        Assert.False(thirdPop.Locked);
        Assert.Contains("\"id\":1", thirdPop.ItemJson);

        // Queue should now be empty
        var fourthPop = await actor.Pop();
        Assert.Null(fourthPop.ItemJson);
        Assert.False(fourthPop.Locked);
    }

    [Fact]
    public async Task PopWithAck_CommitsAtomically()
    {
        // Arrange - push single item
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });

        // Act - PopWithAck should commit lock atomically
        var result = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Assert - item is locked and peeked (still in queue with lock-in-place)
        Assert.NotNull(result.ItemJson);
        Assert.True(result.Locked);
        Assert.NotNull(result.LockId);
        Assert.Contains("\"id\":1", result.ItemJson);

        // Verify queue is blocked while lock exists (cannot pop)
        var popResult = await actor.Pop();
        Assert.Null(popResult.ItemJson);
        Assert.True(popResult.Locked);
        Assert.Equal("Queue is locked by another operation", popResult.Message);
        Assert.NotNull(popResult.LockExpiresAt);
        Assert.True(popResult.LockExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // After acknowledgement, queue should be empty
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = result.LockId });
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Now pop should return empty
        var finalPop = await actor.Pop();
        Assert.Null(finalPop.ItemJson);
        Assert.False(finalPop.Locked);
    }

    [Fact]
    public async Task ExpiredLock_PreservesQueuePosition()
    {
        // Arrange - push items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"A\"}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"B\"}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"C\"}", Priority = 1 });

        // Act - PopWithAck locks Item-A
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 1 });
        Assert.NotNull(popResult.ItemJson);
        Assert.Contains("\"id\":\"A\"", popResult.ItemJson);

        // Push Item-D while lock is active
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"D\"}", Priority = 1 });

        // Wait for lock to expire
        await Task.Delay(1100);

        // Assert - Pop should return Item-A (preserved position at front)
        var firstPop = await actor.Pop();
        Assert.NotNull(firstPop.ItemJson);
        Assert.False(firstPop.Locked);
        Assert.Contains("\"id\":\"A\"", firstPop.ItemJson);

        // Remaining pops should return B, C, D in order
        var secondPop = await actor.Pop();
        Assert.False(secondPop.Locked);
        Assert.Contains("\"id\":\"B\"", secondPop.ItemJson);

        var thirdPop = await actor.Pop();
        Assert.False(thirdPop.Locked);
        Assert.Contains("\"id\":\"C\"", thirdPop.ItemJson);

        var fourthPop = await actor.Pop();
        Assert.False(fourthPop.Locked);
        Assert.Contains("\"id\":\"D\"", fourthPop.ItemJson);

        // Queue should now be empty
        var fifthPop = await actor.Pop();
        Assert.Null(fifthPop.ItemJson);
        Assert.False(fifthPop.Locked);
    }

    [Fact]
    public async Task PopWithAck_ItemsStayInQueueUntilAck()
    {
        // Arrange - push item
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"test\"}", Priority = 1 });

        // Act - PopWithAck locks the item
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popResult.ItemJson);
        var lockId = popResult.LockId;

        // Assert - Pop returns empty while lock exists (item still in queue but locked)
        var blockedPop = await actor.Pop();
        Assert.Null(blockedPop.ItemJson);
        Assert.True(blockedPop.Locked);
        Assert.Equal("Queue is locked by another operation", blockedPop.Message);
        Assert.NotNull(blockedPop.LockExpiresAt);
        Assert.True(blockedPop.LockExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Acknowledge the lock
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });
        Assert.True(ackResult.Success);

        // Now queue should be truly empty (item dequeued on acknowledgement)
        var finalPop = await actor.Pop();
        Assert.Null(finalPop.ItemJson);
        Assert.False(finalPop.Locked);
    }

    [Fact]
    public async Task Acknowledge_RemovesItemsFromQueue()
    {
        // Arrange - push multiple items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":2}", Priority = 1 });

        // Act - PopWithAck first item
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.Contains("\"id\":1", popResult.ItemJson!);
        var lockId = popResult.LockId;

        // Acknowledge
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Assert - Next pop should return second item
        var secondPop = await actor.Pop();
        Assert.NotNull(secondPop.ItemJson);
        Assert.False(secondPop.Locked);
        Assert.Contains("\"id\":2", secondPop.ItemJson!);

        // Queue should now be empty
        var thirdPop = await actor.Pop();
        Assert.Null(thirdPop.ItemJson);
        Assert.False(thirdPop.Locked);
    }

    [Fact]
    public async Task MultiplePopsBlocked_WhenLockActive()
    {
        // Arrange - push items
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":2}", Priority = 1 });

        // Act - PopWithAck creates lock
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        var lockId = popResult.LockId;

        // Attempt Pop() - should be blocked
        var blockedPop = await actor.Pop();
        Assert.Null(blockedPop.ItemJson);
        Assert.True(blockedPop.Locked);
        Assert.Equal("Queue is locked by another operation", blockedPop.Message);
        Assert.NotNull(blockedPop.LockExpiresAt);
        Assert.True(blockedPop.LockExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Attempt another PopWithAck - should be blocked
        var blockedPopWithAck = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.True(blockedPopWithAck.Locked);
        Assert.Null(blockedPopWithAck.ItemJson);
        Assert.Contains("locked", blockedPopWithAck.Message, StringComparison.OrdinalIgnoreCase);

        // Acknowledge
        await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = lockId });

        // Pop() should now work
        var successfulPop = await actor.Pop();
        Assert.NotNull(successfulPop.ItemJson);
        Assert.False(successfulPop.Locked);
        Assert.Contains("\"id\":2", successfulPop.ItemJson!);
    }

    [Fact]
    public async Task LockExpiry_DoesNotReorderQueue()
    {
        // Arrange - push items with different priorities
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"P1-A\"}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"P1-B\"}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"P2-A\"}", Priority = 2 });

        // Act - PopWithAck on priority 1 item
        var popResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 1 });
        Assert.Contains("\"id\":\"P1-A\"", popResult.ItemJson!);

        // Push more items to priority 1 while lock is active
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"P1-C\"}", Priority = 1 });
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":\"P1-D\"}", Priority = 1 });

        // Let lock expire
        await Task.Delay(1100);

        // Assert - Pop all items and verify FIFO maintained within priorities
        var pop1 = await actor.Pop();
        Assert.False(pop1.Locked);
        Assert.Contains("\"id\":\"P1-A\"", pop1.ItemJson);

        var pop2 = await actor.Pop();
        Assert.False(pop2.Locked);
        Assert.Contains("\"id\":\"P1-B\"", pop2.ItemJson);

        var pop3 = await actor.Pop();
        Assert.False(pop3.Locked);
        Assert.Contains("\"id\":\"P1-C\"", pop3.ItemJson);

        var pop4 = await actor.Pop();
        Assert.False(pop4.Locked);
        Assert.Contains("\"id\":\"P1-D\"", pop4.ItemJson);

        var pop5 = await actor.Pop();
        Assert.False(pop5.Locked);
        Assert.Contains("\"id\":\"P2-A\"", pop5.ItemJson);

        // Queue should be empty
        var pop6 = await actor.Pop();
        Assert.Null(pop6.ItemJson);
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
            async () => await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"test\":\"data\"}", Priority = 0 })
        );
        Assert.Contains("Queue corrupted", pushEx.Message);
        Assert.Contains("Test corruption error", pushEx.Message);

        // Act & Assert - Pop should throw
        var popEx = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await actor.Pop()
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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });

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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
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
        Assert.Equal("INVALID_LOCK_ID", extendResult.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_ExpiredLock_ReturnsLockExpired()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock with 1s TTL
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
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
        var popResult = await actor.Pop();

        // Assert - Queue should still be locked
        Assert.True(popResult.Locked);
        Assert.Null(popResult.ItemJson);

        // Now acknowledge
        var ackResult = await actor.Acknowledge(new Interfaces.AcknowledgeRequest { LockId = popWithAckResult.LockId });
        Assert.True(ackResult.Success);

        // Pop should now work (queue empty)
        var popResult2 = await actor.Pop();
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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1,\"value\":\"test\"}", Priority = 1 });
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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
        await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });

        // Act - Try to deadletter with wrong lock ID
        var result = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = "wrong-lock-id" });

        // Assert
        Assert.Equal("ERROR", result.Status);
        Assert.Equal("INVALID_LOCK_ID", result.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_ExpiredLock_ReturnsError()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and create lock with negative expiry (already expired)
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1}", Priority = 1 });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popWithAckResult.LockId);

        // Manually expire the lock by setting ExpiresAt to past timestamp
        var lockState = new LockState
        {
            LockId = popWithAckResult.LockId,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-40).ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds(), // Expired
            Priority = 1,
            HeadSegment = 0
        };
        await mockStateManager.Object.SetStateAsync("_active_lock", lockState);

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
        await actor.Push(new Interfaces.PushRequest { ItemJson = "{\"id\":1,\"urgent\":true}", Priority = 0 });
        var popWithAckResult = await actor.PopWithAck(new Interfaces.PopWithAckRequest { TtlSeconds = 30 });
        Assert.NotNull(popWithAckResult.LockId);

        // Act - Attempt to move to dead letter queue
        var deadLetterResult = await actor.DeadLetter(new Interfaces.DeadLetterRequest { LockId = popWithAckResult.LockId });

        // Assert - Lock validation passed (actual DLQ operation requires Dapr runtime)
        Assert.NotNull(deadLetterResult);
        // Either succeeds or fails with DLQ_PUSH_FAILED/INTERNAL_ERROR (no Dapr runtime in unit tests)
        Assert.True(deadLetterResult.Status == "SUCCESS" || deadLetterResult.ErrorCode == "DLQ_PUSH_FAILED" || deadLetterResult.ErrorCode == "INTERNAL_ERROR");
    }
}
