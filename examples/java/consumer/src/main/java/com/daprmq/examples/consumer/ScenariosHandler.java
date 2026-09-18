package com.daprmq.examples.consumer;

import com.daprmq.client.errors.DaprMQException;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;

import java.io.IOException;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** GET /scenarios and POST /scenarios/{name}/run. */
final class ScenariosHandler implements HttpHandler {
    private static final List<Map<String, Object>> DESCRIPTORS = buildDescriptors();

    private final AppState state;

    ScenariosHandler(AppState state) {
        this.state = state;
    }

    @Override
    public void handle(HttpExchange exchange) throws IOException {
        String path = exchange.getRequestURI().getPath();
        String method = exchange.getRequestMethod();
        try {
            if ("GET".equals(method) && "/scenarios".equals(path)) {
                HttpUtil.sendJson(exchange, 200, DESCRIPTORS);
                return;
            }
            if ("POST".equals(method)) {
                String[] parts = path.split("/");
                if (parts.length == 4 && parts[1].equals("scenarios") && parts[3].equals("run")) {
                    runScenario(exchange, parts[2]);
                    return;
                }
            }
            HttpUtil.sendPlain(exchange, 404);
        } catch (Exception e) {
            Log.error("Unhandled error in /scenarios: " + e.getMessage());
            HttpUtil.sendError(exchange, 500, "INTERNAL_ERROR", String.valueOf(e.getMessage()));
        }
    }

    private void runScenario(HttpExchange exchange, String name) throws IOException {
        if (!isKnownScenario(name)) {
            HttpUtil.sendError(exchange, 404, "UNKNOWN_SCENARIO", "No such scenario: " + name);
            return;
        }
        if (!state.tryBeginScenario()) {
            HttpUtil.sendError(exchange, 409, "SCENARIO_IN_PROGRESS", "A scenario run is already in progress on this pod");
            return;
        }
        try {
            Instant startedAt = Instant.now().truncatedTo(ChronoUnit.MILLIS);
            Log.info("Starting scenario '" + name + "'");
            ConsumerScenarios.ScenarioRun run = ConsumerScenarios.run(name, state.client(), state.queuePrefix());
            Instant finishedAt = Instant.now().truncatedTo(ChronoUnit.MILLIS);
            Log.info("Finished scenario '" + name + "'");

            Map<String, Object> body = new LinkedHashMap<>();
            body.put("scenario", name);
            body.put("role", AppState.ROLE);
            body.put("queueId", run.queueId());
            body.put("startedAt", startedAt.toString());
            body.put("finishedAt", finishedAt.toString());
            body.put("steps", run.steps());
            HttpUtil.sendJson(exchange, 200, body);
        } catch (DaprMQException e) {
            Log.error("Upstream error during scenario '" + name + "': " + e.getMessage());
            HttpUtil.sendError(exchange, 502, "UPSTREAM_ERROR", e.getMessage());
        } finally {
            state.endScenario();
        }
    }

    private static boolean isKnownScenario(String name) {
        return switch (name) {
            case "basic", "ack-deadletter", "priority", "sessions", "idempotency" -> true;
            default -> false;
        };
    }

    private static List<Map<String, Object>> buildDescriptors() {
        List<Map<String, Object>> list = new ArrayList<>();
        list.add(descriptor("basic", "Dequeues up to 5 items and acknowledges each."));
        list.add(descriptor("ack-deadletter", "Dequeues 3 items, acks/dead-letters/lets one expire, then drains the redelivery and the DLQ."));
        list.add(descriptor("priority", "Dequeues up to 10 items in priority order, acknowledging each."));
        list.add(descriptor("sessions", "Consumes session-a then session-b via accept/dequeue/ack/release."));
        list.add(descriptor("idempotency", "Dequeues and acknowledges the survivors of the producer's dedup demo."));
        return list;
    }

    private static Map<String, Object> descriptor(String name, String description) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("name", name);
        m.put("role", AppState.ROLE);
        m.put("description", description);
        return m;
    }
}
