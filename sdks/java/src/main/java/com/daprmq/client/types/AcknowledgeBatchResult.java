package com.daprmq.client.types;

import java.util.List;

/**
 * Result of {@code DaprMQClient.acknowledgeBatch}.
 *
 * @param itemsAcknowledged Number of locks settled by this call.
 * @param results           One entry per requested lock, in request order.
 */
public record AcknowledgeBatchResult(int itemsAcknowledged, List<LockAcknowledgeResult> results) {
}
