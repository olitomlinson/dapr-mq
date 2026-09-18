package com.daprmq.examples.producer;

import com.sun.net.httpserver.HttpExchange;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;

/** Small helpers around {@link HttpExchange} for reading/writing the control-plane JSON bodies. */
final class HttpUtil {
    private HttpUtil() {
    }

    static String readBody(HttpExchange exchange) throws IOException {
        try (InputStream is = exchange.getRequestBody()) {
            return new String(is.readAllBytes(), StandardCharsets.UTF_8);
        }
    }

    static void sendJson(HttpExchange exchange, int status, Object payload) throws IOException {
        byte[] bytes = JsonUtil.writeBytes(payload);
        exchange.getResponseHeaders().set("Content-Type", "application/json");
        exchange.sendResponseHeaders(status, bytes.length);
        try (OutputStream os = exchange.getResponseBody()) {
            os.write(bytes);
        }
    }

    /** Error envelope: {"error": {"code": ..., "message": ...}}. */
    static void sendError(HttpExchange exchange, int status, String code, String message) throws IOException {
        Map<String, Object> error = new LinkedHashMap<>();
        error.put("code", code);
        error.put("message", message);
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("error", error);
        sendJson(exchange, status, body);
    }

    /** A bare status with no body, for unknown-route/method fallbacks. */
    static void sendPlain(HttpExchange exchange, int status) throws IOException {
        exchange.sendResponseHeaders(status, -1);
        exchange.close();
    }
}
