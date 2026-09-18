package com.daprmq.examples.producer;

import com.fasterxml.jackson.databind.JsonNode;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;

import java.io.IOException;
import java.net.URI;
import java.net.URISyntaxException;

/** GET/PUT /config - read or override the DaprMQ connection config, rebuilding the SDK client. */
final class ConfigHandler implements HttpHandler {
    private final AppState state;

    ConfigHandler(AppState state) {
        this.state = state;
    }

    @Override
    public void handle(HttpExchange exchange) throws IOException {
        try {
            switch (exchange.getRequestMethod()) {
                case "GET" -> HttpUtil.sendJson(exchange, 200, state.snapshot().toMap());
                case "PUT" -> handlePut(exchange);
                default -> HttpUtil.sendPlain(exchange, 404);
            }
        } catch (Exception e) {
            Log.error("Unhandled error in /config: " + e.getMessage());
            HttpUtil.sendError(exchange, 500, "INTERNAL_ERROR", String.valueOf(e.getMessage()));
        }
    }

    private void handlePut(HttpExchange exchange) throws IOException {
        String body = HttpUtil.readBody(exchange);
        JsonNode node = JsonUtil.parse(body);
        if (node == null) {
            HttpUtil.sendError(exchange, 400, "INVALID_CONFIG", "Malformed JSON body");
            return;
        }
        String httpBaseUrl = JsonUtil.text(node, "httpBaseUrl");
        String grpcAddress = JsonUtil.text(node, "grpcAddress");
        String queuePrefix = JsonUtil.text(node, "queuePrefix");

        String validationError = validate(httpBaseUrl, grpcAddress, queuePrefix);
        if (validationError != null) {
            HttpUtil.sendError(exchange, 400, "INVALID_CONFIG", validationError);
            return;
        }

        ConfigSnapshot updated = state.applyOverride(httpBaseUrl, grpcAddress, queuePrefix);
        Log.info("Config updated: httpBaseUrl=" + updated.httpBaseUrl() + " grpcAddress=" + updated.grpcAddress()
                + " queuePrefix=" + updated.queuePrefix());
        HttpUtil.sendJson(exchange, 200, updated.toMap());
    }

    private static String validate(String httpBaseUrl, String grpcAddress, String queuePrefix) {
        if (httpBaseUrl != null) {
            if (httpBaseUrl.isBlank()) {
                return "httpBaseUrl must not be empty";
            }
            try {
                URI uri = new URI(httpBaseUrl);
                if (uri.getScheme() == null || uri.getHost() == null) {
                    return "httpBaseUrl must be a valid absolute URL";
                }
            } catch (URISyntaxException e) {
                return "httpBaseUrl must be a valid absolute URL";
            }
        }
        if (grpcAddress != null) {
            if (grpcAddress.isBlank()) {
                return "grpcAddress must not be empty";
            }
            if (grpcAddress.contains("://")) {
                return "grpcAddress must be a bare host:port, no scheme";
            }
            int colon = grpcAddress.lastIndexOf(':');
            if (colon <= 0 || colon == grpcAddress.length() - 1) {
                return "grpcAddress must be in host:port form";
            }
            String portPart = grpcAddress.substring(colon + 1);
            try {
                int port = Integer.parseInt(portPart);
                if (port < 1 || port > 65535) {
                    return "grpcAddress port must be between 1 and 65535";
                }
            } catch (NumberFormatException e) {
                return "grpcAddress port must be numeric";
            }
        }
        if (queuePrefix != null && queuePrefix.isBlank()) {
            return "queuePrefix must not be empty";
        }
        return null;
    }
}
