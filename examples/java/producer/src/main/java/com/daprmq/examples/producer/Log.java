package com.daprmq.examples.producer;

import java.time.Instant;
import java.time.temporal.ChronoUnit;

/** stdout logging in the exact {@code [<language>] [<role>] <ISO8601 UTC> <LEVEL> <message>} format. */
final class Log {
    private Log() {
    }

    static void info(String message) {
        write("INFO", message);
    }

    static void warn(String message) {
        write("WARN", message);
    }

    static void error(String message) {
        write("ERROR", message);
    }

    private static void write(String level, String message) {
        String timestamp = Instant.now().truncatedTo(ChronoUnit.MILLIS).toString();
        System.out.println("[" + AppState.LANGUAGE + "] [" + AppState.ROLE + "] " + timestamp + " " + level + " " + message);
    }
}
