package com.daprmq.client.internal;

import com.daprmq.client.errors.DaprMQException;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

/** Shared Jackson plumbing - not part of the public API. */
public final class Json {
    public static final ObjectMapper MAPPER = new ObjectMapper();

    private Json() {
    }

    public static String write(Object value) {
        try {
            return MAPPER.writeValueAsString(value);
        } catch (Exception e) {
            throw new DaprMQException("Failed to serialize request body: " + e.getMessage());
        }
    }

    public static JsonNode tryParse(String text) {
        if (text == null || text.isBlank()) {
            return null;
        }
        try {
            return MAPPER.readTree(text);
        } catch (Exception e) {
            return null;
        }
    }

    public static JsonNode requireParsed(String text, String url) {
        JsonNode node = tryParse(text);
        if (node == null) {
            throw new DaprMQException("Empty or malformed response body from " + url);
        }
        return node;
    }

    public static String errorMessageFrom(String text, int status) {
        JsonNode node = tryParse(text);
        if (node != null && node.hasNonNull("message")) {
            return node.get("message").asText();
        }
        return "Request failed with status " + status;
    }

    public static String textOrNull(JsonNode node, String field) {
        JsonNode value = node.get(field);
        return (value == null || value.isNull()) ? null : value.asText();
    }
}
