package com.daprmq.examples.producer;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;

import java.io.IOException;
import java.util.LinkedHashMap;
import java.util.Map;

/** POST /reset - clears in-memory scenario bookkeeping and reverts /config to env-derived defaults. */
final class ResetHandler implements HttpHandler {
    private final AppState state;

    ResetHandler(AppState state) {
        this.state = state;
    }

    @Override
    public void handle(HttpExchange exchange) throws IOException {
        if (!"POST".equals(exchange.getRequestMethod())) {
            HttpUtil.sendPlain(exchange, 404);
            return;
        }
        try {
            // Every scenario here runs synchronously end-to-end within a single
            // POST /scenarios/{name}/run call, so there is no cross-run lock/session/lease
            // bookkeeping cached on this AppState to clear - reset only needs to revert /config.
            state.reset();
            Log.info("State reset to defaults");
            Map<String, Object> body = new LinkedHashMap<>();
            body.put("success", true);
            body.put("message", "State reset to defaults");
            HttpUtil.sendJson(exchange, 200, body);
        } catch (Exception e) {
            Log.error("Unhandled error in /reset: " + e.getMessage());
            HttpUtil.sendError(exchange, 500, "INTERNAL_ERROR", String.valueOf(e.getMessage()));
        }
    }
}
