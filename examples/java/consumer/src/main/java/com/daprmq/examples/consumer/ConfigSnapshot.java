package com.daprmq.examples.consumer;

import java.util.LinkedHashMap;
import java.util.Map;

/** Immutable view of the container's current DaprMQ connection config, per GET /config's shape. */
record ConfigSnapshot(String httpBaseUrl, String grpcAddress, String queuePrefix, String language, String role,
                       String source) {
    Map<String, Object> toMap() {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("httpBaseUrl", httpBaseUrl);
        m.put("grpcAddress", grpcAddress);
        m.put("queuePrefix", queuePrefix);
        m.put("language", language);
        m.put("role", role);
        m.put("source", source);
        return m;
    }
}
