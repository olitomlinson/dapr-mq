package com.daprmq.client;

public enum SessionHandlerFailureAction {
    DEAD_LETTER_MESSAGE,
    ABANDON_SESSION,
    BOTH,
    /** Return the message to the front of the session for redelivery. */
    NACK_MESSAGE
}
