package com.daprmq.client.types;

/**
 * Outcome of one lock in a batch acknowledge.
 *
 * @param lockId  The lock id as sent.
 * @param outcome One of the {@link AcknowledgeOutcome} values.
 */
public record LockAcknowledgeResult(String lockId, String outcome) {
}
