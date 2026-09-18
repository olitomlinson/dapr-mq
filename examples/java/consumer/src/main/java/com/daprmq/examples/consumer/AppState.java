package com.daprmq.examples.consumer;

import com.daprmq.client.DaprMQClient;

import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Holds the mutable DaprMQ connection config and the in-process {@link DaprMQClient}, shared
 * across the HTTP server's request-handling threads. Config mutation ({@code PUT /config},
 * {@code POST /reset}) is guarded by {@code configLock}; the client reference itself is an
 * {@link AtomicReference} so readers never observe a half-built client.
 */
final class AppState {
    static final String LANGUAGE = "java";
    static final String ROLE = "consumer";

    private final String defaultHttpBaseUrl;
    private final String defaultGrpcAddress;
    private final String defaultQueuePrefix;

    private final Object configLock = new Object();
    private String httpBaseUrl;
    private String grpcAddress;
    private String queuePrefix;
    private String source = "default";

    private final AtomicReference<DaprMQClient> client = new AtomicReference<>();
    private final ReentrantLock scenarioLock = new ReentrantLock();

    AppState(String httpBaseUrl, String grpcAddress, String queuePrefix) {
        this.defaultHttpBaseUrl = httpBaseUrl;
        this.defaultGrpcAddress = grpcAddress;
        this.defaultQueuePrefix = queuePrefix;
        this.httpBaseUrl = httpBaseUrl;
        this.grpcAddress = grpcAddress;
        this.queuePrefix = queuePrefix;
        this.client.set(DaprMQClient.create(httpBaseUrl, grpcAddress));
    }

    DaprMQClient client() {
        return client.get();
    }

    ConfigSnapshot snapshot() {
        synchronized (configLock) {
            return new ConfigSnapshot(httpBaseUrl, grpcAddress, queuePrefix, LANGUAGE, ROLE, source);
        }
    }

    String queuePrefix() {
        synchronized (configLock) {
            return queuePrefix;
        }
    }

    /** Applies a {@code PUT /config} override; {@code null} arguments keep their current value. */
    ConfigSnapshot applyOverride(String newHttpBaseUrl, String newGrpcAddress, String newQueuePrefix) {
        synchronized (configLock) {
            String h = newHttpBaseUrl != null ? newHttpBaseUrl : httpBaseUrl;
            String g = newGrpcAddress != null ? newGrpcAddress : grpcAddress;
            String q = newQueuePrefix != null ? newQueuePrefix : queuePrefix;
            swapClient(h, g);
            httpBaseUrl = h;
            grpcAddress = g;
            queuePrefix = q;
            source = "override";
            return new ConfigSnapshot(httpBaseUrl, grpcAddress, queuePrefix, LANGUAGE, ROLE, source);
        }
    }

    /** Reverts to the env-derived defaults captured at startup. */
    ConfigSnapshot reset() {
        synchronized (configLock) {
            httpBaseUrl = defaultHttpBaseUrl;
            grpcAddress = defaultGrpcAddress;
            queuePrefix = defaultQueuePrefix;
            source = "default";
            swapClient(httpBaseUrl, grpcAddress);
            return new ConfigSnapshot(httpBaseUrl, grpcAddress, queuePrefix, LANGUAGE, ROLE, source);
        }
    }

    private void swapClient(String newHttpBaseUrl, String newGrpcAddress) {
        DaprMQClient old = client.getAndSet(DaprMQClient.create(newHttpBaseUrl, newGrpcAddress));
        if (old != null) {
            // close() can block briefly waiting for the owned gRPC channel to terminate; do it off
            // the request thread so PUT /config and POST /reset respond promptly.
            Thread closer = new Thread(old::close, "daprmq-client-closer");
            closer.setDaemon(true);
            closer.start();
        }
    }

    /** One simple in-process mutex per pod, per API_CONTRACT.md's SCENARIO_IN_PROGRESS rule. */
    boolean tryBeginScenario() {
        return scenarioLock.tryLock();
    }

    void endScenario() {
        scenarioLock.unlock();
    }
}
