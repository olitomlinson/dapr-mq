package com.daprmq.client;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;

/** Fake HTTP server answering each request with the next scripted response; the last one repeats. */
final class ScriptedHttpServer implements AutoCloseable {
    record Answer(int status, String body, Map<String, String> headers) {
    }

    record Recorded(Map<String, String> headers, String body) {
    }

    static final Answer NOT_DELIVERED = new Answer(503,
            "{\"message\":\"unavailable\",\"success\":false,\"errorCode\":\"UNAVAILABLE\"}", Map.of("daprmq-delivery", "not-delivered"));
    static final Answer UNKNOWN = new Answer(504,
            "{\"message\":\"unknown\",\"success\":false,\"errorCode\":\"DELIVERY_UNKNOWN\"}", Map.of("daprmq-delivery", "unknown"));

    static Answer ok(String body) {
        return new Answer(200, body, Map.of());
    }

    private final HttpServer server;
    private final List<Answer> answers;
    private final List<Recorded> requests = new CopyOnWriteArrayList<>();
    private volatile long delayMillis;

    ScriptedHttpServer(Answer... answers) {
        this.answers = List.of(answers);
        try {
            server = HttpServer.create(new InetSocketAddress("localhost", 0), 0);
        } catch (IOException e) {
            throw new RuntimeException(e);
        }
        server.createContext("/", this::handle);
        server.start();
    }

    /** Answers each request only after this delay (a slow but working server). */
    ScriptedHttpServer withDelay(java.time.Duration delay) {
        delayMillis = delay.toMillis();
        return this;
    }

    String baseUrl() {
        return "http://localhost:" + server.getAddress().getPort();
    }

    List<Recorded> requests() {
        return requests;
    }

    private void handle(HttpExchange exchange) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        exchange.getRequestBody().transferTo(buffer);
        Map<String, String> headers = new HashMap<>();
        exchange.getRequestHeaders().forEach((k, v) -> headers.put(k.toLowerCase(), v.isEmpty() ? "" : v.get(0)));
        requests.add(new Recorded(headers, buffer.toString(StandardCharsets.UTF_8)));

        if (delayMillis > 0) {
            try {
                Thread.sleep(delayMillis);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }
        Answer answer = answers.get(Math.min(requests.size(), answers.size()) - 1);
        answer.headers().forEach((k, v) -> exchange.getResponseHeaders().add(k, v));
        byte[] bytes = answer.body().getBytes(StandardCharsets.UTF_8);
        exchange.getResponseHeaders().add("content-type", "application/json");
        exchange.sendResponseHeaders(answer.status(), bytes.length == 0 ? -1 : bytes.length);
        if (bytes.length > 0) {
            exchange.getResponseBody().write(bytes);
        }
        exchange.close();
    }

    @Override
    public void close() {
        server.stop(0);
    }
}
