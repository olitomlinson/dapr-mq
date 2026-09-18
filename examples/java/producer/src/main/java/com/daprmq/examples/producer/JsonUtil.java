package com.daprmq.examples.producer;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

/** Small Jackson wrapper for the control-plane HTTP bodies (request parsing, response writing). */
final class JsonUtil {
    static final ObjectMapper MAPPER = new ObjectMapper();

    private JsonUtil() {
    }

    /** Parses a request body; an empty/blank body parses as an empty object. Returns null on malformed JSON. */
    static JsonNode parse(String body) {
        if (body == null || body.isBlank()) {
            return MAPPER.createObjectNode();
        }
        try {
            return MAPPER.readTree(body);
        } catch (Exception e) {
            return null;
        }
    }

    static String text(JsonNode node, String field) {
        JsonNode v = node.get(field);
        return (v == null || v.isNull()) ? null : v.asText();
    }

    static byte[] writeBytes(Object payload) {
        try {
            return MAPPER.writeValueAsBytes(payload);
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }
}
