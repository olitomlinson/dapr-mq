package com.daprmq.client.errors;

/**
 * Thrown by {@code DaprMQClient.consumeSession} when a session was successfully claimed but its
 * lease could not be maintained afterwards (server sent a terminal SessionLost frame) - distinct
 * from a claim that never succeeded in the first place.
 */
public class SessionLostException extends DaprMQException {
    public SessionLostException(String message) {
        super(message, "SESSION_LOST");
    }
}
