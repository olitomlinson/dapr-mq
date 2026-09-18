package com.daprmq.client.errors;

public class SessionActorUnavailableException extends DaprMQException {
    public SessionActorUnavailableException(String message) {
        super(message, "SESSION_ACTOR_UNAVAILABLE");
    }
}
