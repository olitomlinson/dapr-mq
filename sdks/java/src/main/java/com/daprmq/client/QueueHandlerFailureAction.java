package com.daprmq.client;

public enum QueueHandlerFailureAction {
    /** Return the message to its original position for redelivery (+1 delivery count). */
    NACK,
    DEAD_LETTER
}
