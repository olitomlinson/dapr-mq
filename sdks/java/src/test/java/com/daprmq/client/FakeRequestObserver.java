package com.daprmq.client;

import com.daprmq.grpc.ConsumeSessionRequest;
import io.grpc.stub.ClientCallStreamObserver;

import java.util.ArrayList;
import java.util.List;

/** Records what {@link SessionStream} writes onto the (fake) request stream. */
final class FakeRequestObserver extends ClientCallStreamObserver<ConsumeSessionRequest> {
    final List<ConsumeSessionRequest> sent = new ArrayList<>();
    volatile boolean cancelled;
    volatile boolean completed;

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
