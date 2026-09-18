package com.daprmq.examples.consumer;

import com.sun.net.httpserver.HttpServer;

import java.net.InetSocketAddress;
import java.util.concurrent.Executors;

/** Entry point: reads env vars, builds the DaprMQ client, and starts the control-plane HTTP server. */
public final class Main {
    private Main() {
    }

    public static void main(String[] args) throws Exception {
        int controlPort = Integer.parseInt(env("CONTROL_PORT", "8080"));
        String httpBaseUrl = env("DAPRMQ_HTTP_BASE_URL", "http://localhost:8002");
        String grpcAddress = env("DAPRMQ_GRPC_ADDRESS", "localhost:8003");
        String queuePrefix = env("DAPRMQ_QUEUE_PREFIX", "examples-java");

        AppState state = new AppState(httpBaseUrl, grpcAddress, queuePrefix);

        HttpServer server = HttpServer.create(new InetSocketAddress(controlPort), 0);
        server.createContext("/health", new HealthHandler());
        server.createContext("/config", new ConfigHandler(state));
        server.createContext("/reset", new ResetHandler(state));
        server.createContext("/scenarios", new ScenariosHandler(state));
        // A cached thread pool so a slow scenario run (ack-deadletter's ~11s expiry wait) never
        // blocks concurrent /health probes or other control-plane requests.
        server.setExecutor(Executors.newCachedThreadPool());
        server.start();

        Log.info("DaprMQ Java consumer example listening on port " + controlPort
                + " (daprmqHttpBaseUrl=" + httpBaseUrl + ", daprmqGrpcAddress=" + grpcAddress
                + ", queuePrefix=" + queuePrefix + ")");
    }

    private static String env(String name, String fallback) {
        String value = System.getenv(name);
        return (value == null || value.isBlank()) ? fallback : value;
    }
}
