package com.daprmq.client.types;

public record EnqueueResult(boolean success, String message, int itemsEnqueued, int itemsDeduplicated) {
}
