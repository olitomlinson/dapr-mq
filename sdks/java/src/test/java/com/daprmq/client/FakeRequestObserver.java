package com.daprmq.client;

import com.daprmq.grpc.ConsumeSessionRequest;
import io.grpc.stub.ClientCallStreamObserver;

import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;

/** Records what {@link SessionStream} writes onto the (fake) request stream. */
final class FakeRequestObserver extends ClientCallStreamObserver<ConsumeSessionRequest> {
    /** Written by the consumer's slot thread, read by the test thread. */
    final List<ConsumeSessionRequest> sent = new CopyOnWriteArrayList<>();
    volatile boolean cancelled;
    volatile boolean completed;
    /** Runs when the client half-closes - e.g. a fake server ending the response stream. */
    volatile Runnable onHalfClose = () -> { };

    @Override
    public void cancel(String message, Throwable cause) {
        cancelled = true;
    }

    @Override
    public void onNext(ConsumeSessionRequest value) {
        sent.add(value);
    }

    @Override
    public void onError(Throwable t) {
    }

    @Override
    public void onCompleted() {
        completed = true;
        onHalfClose.run();
    }

    @Override
    public boolean isReady() {
        return true;
    }

    @Override
    public void setOnReadyHandler(Runnable onReadyHandler) {
    }

    @Override
    public void disableAutoInboundFlowControl() {
    }

    @Override
    public void request(int count) {
    }

    @Override
    public void setMessageCompression(boolean enable) {
    }
}
