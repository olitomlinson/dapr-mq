package com.daprmq.client;

public enum SessionHandlerFailureAction {
    DEAD_LETTER_MESSAGE,
    ABANDON_SESSION,
    BOTH
}
