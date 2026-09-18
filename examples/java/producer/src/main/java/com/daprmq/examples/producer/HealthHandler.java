package com.daprmq.examples.producer;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;

import java.io.IOException;
import java.util.LinkedHashMap;
import java.util.Map;

/** GET /health - always 200 once the process is accepting connections. */
final class HealthHandler implements HttpHandler {
    @Override
    public void handle(HttpExchange exchange) throws IOException {
        if (!"GET".equals(exchange.getRequestMethod())) {
            HttpUtil.sendPlain(exchange, 404);
            return;
        }
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("status", "ok");
        body.put("language", AppState.LANGUAGE);
        body.put("role", AppState.ROLE);
        HttpUtil.sendJson(exchange, 200, body);
    }
}
