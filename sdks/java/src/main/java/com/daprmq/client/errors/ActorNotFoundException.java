package com.daprmq.client.errors;

public class ActorNotFoundException extends DaprMQException {
    public ActorNotFoundException(String message) {
        super(message, "ACTOR_NOT_FOUND");
    }
}
