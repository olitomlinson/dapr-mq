package com.daprmq.client;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

/** Minimal single-response fake HTTP server for client tests - no external mocking library needed. */
final class FakeHttpServer implements AutoCloseable {
    private final HttpServer server;
    private volatile int nextStatus = 200;
    private volatile String nextBody = "";
    private volatile RecordedRequest lastRequest;

    record RecordedRequest(String method, String path, Map<String, String> headers, String body) {
    }

    FakeHttpServer() {
        try {
            server = HttpServer.create(new InetSocketAddress("localhost", 0), 0);
        } catch (IOException e) {
            throw new RuntimeException(e);
        }
        server.createContext("/", this::handle);
        server.start();
    }

    void respondWith(int status, String body) {
        this.nextStatus = status;
        this.nextBody = body == null ? "" : body;
    }

    RecordedRequest lastRequest() {
        return lastRequest;
    }

    String baseUrl() {
        return "http://localhost:" + server.getAddress().getPort();
    }

    private void handle(HttpExchange exchange) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        exchange.getRequestBody().transferTo(buffer);
        String body = buffer.toString(StandardCharsets.UTF_8);

        Map<String, String> headers = new HashMap<>();
        exchange.getRequestHeaders().forEach((k, v) -> headers.put(k.toLowerCase(), v.isEmpty() ? "" : v.get(0)));

        lastRequest = new RecordedRequest(exchange.getRequestMethod(), exchange.getRequestURI().getPath(), headers, body);

        byte[] responseBytes = nextBody.getBytes(StandardCharsets.UTF_8);
        int status = nextStatus;
        if (status == 204 || responseBytes.length == 0) {
            exchange.sendResponseHeaders(status, -1);
        } else {
            exchange.getResponseHeaders().add("content-type", "application/json");
            exchange.sendResponseHeaders(status, responseBytes.length);
            exchange.getResponseBody().write(responseBytes);
        }
        exchange.close();
    }

    @Override
    public void close() {
        server.stop(0);
    }
}
