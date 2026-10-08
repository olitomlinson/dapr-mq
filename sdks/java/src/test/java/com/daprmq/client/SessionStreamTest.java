package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.SessionLockedException;
import com.daprmq.client.errors.SessionLostException;
import com.daprmq.client.types.SessionDelivery;
import com.daprmq.grpc.ConsumeSessionRequest;
import com.daprmq.grpc.ConsumeSessionResponse;
import com.daprmq.grpc.SessionAssigned;
import com.daprmq.grpc.SessionDelivered;
import com.daprmq.grpc.SessionError;
import com.daprmq.grpc.SessionLost;
import io.grpc.stub.StreamObserver;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.util.Iterator;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class SessionStreamTest {

    private FakeRequestObserver requestObserver;
    private StreamObserver<ConsumeSessionResponse> responseObserver;

    private final long originalDrainTimeout = SessionStream.drainTimeoutMillis;

    @AfterEach
    void restoreDrainTimeout() {
        SessionStream.drainTimeoutMillis = originalDrainTimeout;
    }

    private static ConsumeSessionResponse delivered(String lockId) {
        return ConsumeSessionResponse.newBuilder()
                .setDelivered(SessionDelivered.newBuilder().setLockId(lockId).setItemJson("{}").setPriority(0).setLockExpiresAt(1).build())
                .build();
    }

    private SessionStream newStream(ConsumeSessionOptions options) {
        requestObserver = new FakeRequestObserver();
        SessionStream.StreamFactory factory = observer -> {
            responseObserver = observer;
            return requestObserver;
        };
        return new SessionStream(factory, "my-queue", options);
    }

    @Test
    void sendsStartFrameImmediately() {
        newStream(new ConsumeSessionOptions("order-42", 45, 5));

        assertEquals(1, requestObserver.sent.size());
        var start = requestObserver.sent.get(0).getStart();
        assertEquals("my-queue", start.getQueueId());
        assertEquals("order-42", start.getSessionId());
        assertEquals(45, start.getLeaseSeconds());
        assertEquals(5, start.getPrefetchCount());
    }

    @Test
    void deliversItemsAfterSessionAssignedAndAcksWriteToTheStream() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());

        responseObserver.onNext(ConsumeSessionResponse.newBuilder()
                .setSessionAssigned(SessionAssigned.newBuilder().setSessionId("s1").setLeaseExpiresAt(100).build())
                .build());
        responseObserver.onNext(ConsumeSessionResponse.newBuilder()
                .setDelivered(SessionDelivered.newBuilder()
                        .setLockId("L1")
                        .setItemJson("{\"task\":\"x\"}")
                        .setPriority(0)
                        .setLockExpiresAt(123.0)
                        .build())
                .build());
        responseObserver.onCompleted();

        Iterator<SessionDelivery> it = stream.iterator();
        assertTrue(it.hasNext());
        SessionDelivery delivery = it.next();
        assertEquals("s1", delivery.sessionId());
        assertEquals("L1", delivery.lockId());
        assertEquals("x", delivery.item().get("task").asText());

        delivery.ack().run();
        assertEquals("L1", requestObserver.sent.get(1).getAck().getLockId());

        assertFalse(it.hasNext());
    }

    @Test
    void deadLetterWritesToTheStream() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(ConsumeSessionResponse.newBuilder()
                .setDelivered(SessionDelivered.newBuilder().setLockId("L2").setItemJson("{}").setPriority(1).setLockExpiresAt(1).build())
                .build());
        responseObserver.onCompleted();

        SessionDelivery delivery = stream.iterator().next();
        delivery.deadLetter().run();

        assertEquals("L2", requestObserver.sent.get(1).getDeadLetter().getLockId());
    }

    @Test
    void nackWritesToTheStream() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(ConsumeSessionResponse.newBuilder()
                .setDelivered(SessionDelivered.newBuilder().setLockId("L3").setItemJson("{}").setPriority(1).setLockExpiresAt(1).build())
                .build());
        responseObserver.onCompleted();

        SessionDelivery delivery = stream.iterator().next();
        delivery.nack().run();

        assertEquals("L3", requestObserver.sent.get(1).getNack().getLockId());
    }

    @Test
    void errorFrameThrowsMappedException() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(ConsumeSessionResponse.newBuilder()
                .setError(SessionError.newBuilder().setErrorCode("SESSION_LOCKED").setMessage("locked").build())
                .build());

        Iterator<SessionDelivery> it = stream.iterator();
        assertThrows(SessionLockedException.class, it::hasNext);
    }

    @Test
    void sessionLostFrameThrowsSessionLostException() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(ConsumeSessionResponse.newBuilder()
                .setSessionLost(SessionLost.newBuilder().setMessage("lost the lease").build())
                .build());

        Iterator<SessionDelivery> it = stream.iterator();
        assertThrows(SessionLostException.class, it::hasNext);
    }

    @Test
    void cancelInvokesTheUnderlyingClientCallCancel() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());

        stream.cancel();

        assertTrue(requestObserver.cancelled);
    }

    @Test
    void closeCompletesTheRequestStream() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());

        stream.close();

        assertTrue(requestObserver.completed);
    }

    @Test
    void closeAfterAckingHalfClosesAndLeavesTheCallToTheServer() throws InterruptedException {
        SessionStream.drainTimeoutMillis = 100;
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(delivered("L1"));
        Iterator<SessionDelivery> it = stream.iterator();
        it.next().ack().run();

        stream.close();
        responseObserver.onCompleted(); // the server applies the ack, then ends the stream

        assertFalse(it.hasNext());
        Thread.sleep(300); // past the drain timeout
        assertEquals("L1", requestObserver.sent.get(1).getAck().getLockId());
        assertTrue(requestObserver.completed);
        assertFalse(requestObserver.cancelled, "the call must not be cancelled once the server has ended it");
    }

    @Test
    void closeCancelsTheCallIfTheServerNeverEndsIt() throws InterruptedException {
        SessionStream.drainTimeoutMillis = 100;
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());

        stream.close();

        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
        while (!requestObserver.cancelled && System.nanoTime() < deadline) {
            Thread.sleep(10);
        }
        assertTrue(requestObserver.completed);
        assertTrue(requestObserver.cancelled);
    }

    @Test
    void afterCloseNothingMoreIsHandedOut() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(delivered("L1"));
        Iterator<SessionDelivery> it = stream.iterator();
        assertEquals("L1", it.next().lockId());

        stream.close();
        responseObserver.onNext(delivered("L2"));
        responseObserver.onCompleted();

        assertFalse(it.hasNext());
    }

    @Test
    void settlingAfterCloseThrowsWithoutWriting() {
        SessionStream stream = newStream(ConsumeSessionOptions.defaults());
        responseObserver.onNext(delivered("L1"));
        SessionDelivery delivery = stream.iterator().next();

        stream.close();

        assertThrows(DaprMQException.class, () -> delivery.ack().run());
        assertEquals(1, requestObserver.sent.size()); // just the Start frame
    }
}
