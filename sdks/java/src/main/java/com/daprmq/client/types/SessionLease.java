package com.daprmq.client.types;

public record SessionLease(String sessionId, String leaseId, double leaseExpiresAt) {
}
