import { LANGUAGE, ROLE } from "./constants.js";

export type LogLevel = "INFO" | "WARN" | "ERROR";

/**
 * Logs to stdout in the fixed control-plane format:
 * `[<language>] [<role>] <ISO8601 UTC timestamp> <LEVEL> <message>`
 * (see examples/shared/API_CONTRACT.md#logging)
 */
export function log(level: LogLevel, message: string): void {
  const timestamp = new Date().toISOString();
  console.log(`[${LANGUAGE}] [${ROLE}] ${timestamp} ${level} ${message}`);
}
