package com.daprmq.client;

import com.daprmq.client.perf.Metrics.StreamRecord;
import com.daprmq.grpc.ConsumeSessionResponse;
import com.daprmq.grpc.DaprMQGrpc;
import io.grpc.ManagedChannel;
import io.grpc.stub.StreamObserver;

import java.util.List;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.function.DoubleSupplier;

/**
 * Perf harness only (it lives in this package for SessionStream's stream factory): the client
 * handed to SessionQueueConsumer, timestamping each consumeSession stream - opened, first
 * delivery, ended by the server - as the .NET harness's RecordingDaprMQClient does. With the
 * handler intervals that accounts for every slot-second.
 */
public final class RecordingSessionClient implements SessionCapableClient {
    private final DaprMQGrpc.DaprMQStub stub;
    private final DoubleSupplier clockMs;
    private final ConcurrentLinkedQueue<StreamRecord> streams = new ConcurrentLinkedQueue<>();

    public RecordingSessionClient(ManagedChannel channel, DoubleSupplier clockMs) {
        this.stub = DaprMQGrpc.newStub(channel);
        this.clockMs = clockMs;
    }

    public List<StreamRecord> streams() {
        return List.copyOf(streams);
    }

    @Override
    public SessionStream consumeSession(String queueId, ConsumeSessionOptions options) {
        double openMs = clockMs.getAsDouble();
        return new SessionStream(responseObserver -> stub.consumeSession(new StreamObserver<>() {
            private Double firstDeliveryMs;
            private String sessionId;

            @Override
            public void onNext(ConsumeSessionResponse value) {
                if (firstDeliveryMs == null && value.hasDelivered()) {
                    firstDeliveryMs = clockMs.getAsDouble();
                }
                if (value.hasSessionAssigned()) {
                    sessionId = value.getSessionAssigned().getSessionId();
                }
                responseObserver.onNext(value);
            }

            @Override
            public void onError(Throwable t) {
                record(t.getClass().getSimpleName());
                responseObserver.onError(t);
            }

            @Override
            public void onCompleted() {
                record("completed");
                responseObserver.onCompleted();
            }

            private void record(String endReason) {
                streams.add(new StreamRecord(openMs, firstDeliveryMs, clockMs.getAsDouble(), firstDeliveryMs == null ? null : sessionId, endReason));
            }
        }), queueId, options);
    }
}
