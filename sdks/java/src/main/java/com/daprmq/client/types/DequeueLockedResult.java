package com.daprmq.client.types;

import java.util.List;

public record DequeueLockedResult(List<DequeueLockedItem> items, boolean locked, String message) {
}
