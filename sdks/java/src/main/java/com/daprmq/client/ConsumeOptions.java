package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;

import java.time.Duration;
import java.util.function.BiConsumer;

/**
 * @param prefetchCount           How many delivered but unsettled items to keep in flight (1-1000,
 *                                default: 1). Above 1, a nack can reorder the queue.
 * @param lockTtl                 Lock time-to-live, sent in whole seconds rounded up (default: 30 s).
 *                                The server renews each delivered item's lock until it is settled, so
 *                                this only bounds how long an item stays locked after this client
 *                                disappears without closing the stream.
 * @param allowCompetingConsumers Let this stream hold locks while other consumers hold theirs
 *                                (default: false - one lock holder at a time).
 * @param onSettleFailed          Called with the lock id and error when the server rejects an ack,
 *                                nack or dead-letter. The stream carries on. May be {@code null}.
 */
public record ConsumeOptions(
        int prefetchCount,
        Duration lockTtl,
        boolean allowCompetingConsumers,
        BiConsumer<String, DaprMQException> onSettleFailed) {
    public static ConsumeOptions defaults() {
        return new ConsumeOptions(1, Duration.ofSeconds(30), false, null);
    }
}
