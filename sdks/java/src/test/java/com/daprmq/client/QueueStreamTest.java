package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.LockNotFoundException;
import com.daprmq.client.errors.StreamClosedException;
import com.daprmq.client.errors.ValidationException;
import com.daprmq.client.types.QueueDelivery;
import com.daprmq.grpc.ConsumeDelivered;
import com.daprmq.grpc.ConsumeError;
import com.daprmq.grpc.ConsumeRequest;
import com.daprmq.grpc.ConsumeResponse;
import com.daprmq.grpc.ConsumeSettleFailed;
import io.grpc.stub.StreamObserver;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.time.Duration;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class QueueStreamTest {

    private FakeRequestObserver<ConsumeRequest> requestObserver;
    private StreamObserver<ConsumeResponse> responseObserver;

    private final long originalDrainTimeout = QueueStream.drainTimeoutMillis;

    @AfterEach
    void restoreDrainTimeout() {
        QueueStream.drainTimeoutMillis = originalDrainTimeout;
    }

    private static ConsumeResponse delivered(String lockId, int deliveryCount) {
        return ConsumeResponse.newBuilder()
                .setDelivered(ConsumeDelivered.newBuilder().setLockId(lockId).setItemJson("{\"task\":\"x\"}")
                        .setPriority(1).setLockExpiresAt(123.0).setDeliveryCount(deliveryCount).build())
                .build();
    }

    private QueueStream newStream(ConsumeOptions options) {
        requestObserver = new FakeRequestObserver<>();
        QueueStream.StreamFactory factory = observer -> {
            responseObserver = observer;
            return requestObserver;
        };
        return new QueueStream(factory, "my-queue", options);
    }

    @Test
    void sendsTheDefaultsOnTheStartFrame() {
        newStream(ConsumeOptions.defaults());

        var start = requestObserver.sent.get(0).getStart();
        assertEquals("my-queue", start.getQueueId());
        assertEquals(1, start.getPrefetchCount());
        assertEquals(30, start.getLockTtlSeconds());
        assertFalse(start.getAllowCompetingConsumers());
    }

    @Test
    void sendsTheOptionsOnTheStartFrameWithTheLockTtlRoundedUp() {
        newStream(new ConsumeOptions(50, Duration.ofMillis(10_200), true, null));

        var start = requestObserver.sent.get(0).getStart();
        assertEquals(50, start.getPrefetchCount());
        assertEquals(11, start.getLockTtlSeconds());
        assertTrue(start.getAllowCompetingConsumers());
    }

    @Test
    void yieldsDeliveriesWhoseSettlesWriteFrames() {
        QueueStream stream = newStream(ConsumeOptions.defaults());
        responseObserver.onNext(delivered("L1", 2));
        responseObserver.onNext(delivered("L2", 1));
        responseObserver.onNext(delivered("L3", 1));

        Iterator<QueueDelivery> it = stream.iterator();
        QueueDelivery d1 = it.next();
        d1.ack().run();
        it.next().nack().run();
        it.next().deadLetter().run();

        assertEquals("L1", d1.lockId());
        assertEquals("x", d1.item().get("task").asText());
        assertEquals(1, d1.priority());
        assertEquals(123.0, d1.lockExpiresAt());
        assertEquals(2, d1.deliveryCount());
        assertEquals("L1", requestObserver.sent.get(1).getAck().getLockId());
        assertEquals("L2", requestObserver.sent.get(2).getNack().getLockId());
        assertEquals("L3", requestObserver.sent.get(3).getDeadLetter().getLockId());
    }

    @Test
    void aSettleFailedFrameGoesToTheCallbackAndTheStreamCarriesOn() {
        List<Map.Entry<String, DaprMQException>> failures = new ArrayList<>();
        QueueStream stream = newStream(new ConsumeOptions(1, Duration.ofSeconds(30), false, (lockId, err) -> failures.add(Map.entry(lockId, err))));
        responseObserver.onNext(ConsumeResponse.newBuilder()
                .setSettleFailed(ConsumeSettleFailed.newBuilder().setLockId("L0").setErrorCode("LOCK_NOT_FOUND").setMessage("gone"))
                .build());
        responseObserver.onNext(delivered("L1", 1));
        responseObserver.onCompleted();

        List<String> seen = new ArrayList<>();
        for (QueueDelivery d : stream) {
            seen.add(d.lockId());
        }

        assertEquals(List.of("L1"), seen);
        assertEquals(1, failures.size());
        assertEquals("L0", failures.get(0).getKey());
        assertInstanceOf(LockNotFoundException.class, failures.get(0).getValue());
    }

    @Test
    void anErrorFrameThrowsTheMappedException() {
        QueueStream stream = newStream(ConsumeOptions.defaults());
        responseObserver.onNext(ConsumeResponse.newBuilder()
                .setError(ConsumeError.newBuilder().setErrorCode("INVALID_ARGUMENT").setMessage("bad start"))
                .build());

        assertThrows(ValidationException.class, () -> stream.iterator().hasNext());
    }

    @Test
    void settlingAfterTheStreamEndedThrowsStreamClosed() {
        QueueStream stream = newStream(ConsumeOptions.defaults());
        responseObserver.onNext(delivered("L1", 1));
        responseObserver.onCompleted();

        List<QueueDelivery> kept = new ArrayList<>();
        stream.forEach(kept::add);

        assertThrows(StreamClosedException.class, () -> kept.get(0).ack().run());
    }

    @Test
    void closeHalfClosesHandsOutNothingMoreAndWaitsForTheServer() {
        QueueStream stream = newStream(ConsumeOptions.defaults());
        responseObserver.onNext(delivered("L1", 1));
        requestObserver.onHalfClose = () -> {
            responseObserver.onNext(delivered("L2", 1));
            responseObserver.onCompleted();
        };

        List<String> seen = new ArrayList<>();
        for (QueueDelivery d : stream) {
            seen.add(d.lockId());
            stream.close();
        }

        assertEquals(List.of("L1"), seen);
        assertTrue(requestObserver.completed);
        assertFalse(requestObserver.cancelled);
    }

    @Test
    void cancelsTheCallIfTheServerNeverEndsItAfterClose() throws InterruptedException {
        QueueStream.drainTimeoutMillis = 100;
        QueueStream stream = newStream(ConsumeOptions.defaults());

        stream.close();
        Iterator<QueueDelivery> it = stream.iterator();
        assertFalse(it.hasNext());

        TimeUnit.MILLISECONDS.sleep(50);
        assertTrue(requestObserver.cancelled);
    }
}
