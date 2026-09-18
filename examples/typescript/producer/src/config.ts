import { DaprMQClient } from "daprmq-client";
import { LANGUAGE, ROLE } from "./constants.js";

export interface DaprMQRuntimeConfig {
  httpBaseUrl: string;
  grpcAddress: string;
  queuePrefix: string;
}

export interface ConfigOverrideInput {
  httpBaseUrl?: unknown;
  grpcAddress?: unknown;
  queuePrefix?: unknown;
}

export type ConfigSource = "default" | "override";

export interface ConfigResponse extends DaprMQRuntimeConfig {
  language: typeof LANGUAGE;
  role: typeof ROLE;
  source: ConfigSource;
}

export class InvalidConfigError extends Error {}

function readDefaultConfig(): DaprMQRuntimeConfig {
  const httpBaseUrl = process.env.DAPRMQ_HTTP_BASE_URL?.trim();
  const grpcAddress = process.env.DAPRMQ_GRPC_ADDRESS?.trim();
  const queuePrefix = process.env.DAPRMQ_QUEUE_PREFIX?.trim();

  return {
    httpBaseUrl: httpBaseUrl || "http://localhost:8002",
    grpcAddress: grpcAddress || "localhost:8003",
    queuePrefix: queuePrefix || `examples-${LANGUAGE}`,
  };
}

function isValidHttpUrl(value: string): boolean {
  try {
    const parsed = new URL(value);
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch {
    return false;
  }
}

function isValidGrpcAddress(value: string): boolean {
  if (!value || /\s/.test(value) || value.includes("://")) {
    return false;
  }
  const parts = value.split(":");
  if (parts.length !== 2 || !parts[0]) {
    return false;
  }
  const port = Number(parts[1]);
  return Number.isInteger(port) && port > 0 && port <= 65535;
}

function buildClient(config: DaprMQRuntimeConfig): DaprMQClient {
  return new DaprMQClient({ httpBaseUrl: config.httpBaseUrl, grpcAddress: config.grpcAddress });
}

let config: DaprMQRuntimeConfig = readDefaultConfig();
let source: ConfigSource = "default";
let client: DaprMQClient = buildClient(config);

export function getClient(): DaprMQClient {
  return client;
}

export function getQueuePrefix(): string {
  return config.queuePrefix;
}

export function getConfigResponse(): ConfigResponse {
  return { ...config, language: LANGUAGE, role: ROLE, source };
}

/**
 * Applies a partial config override (PUT /config): rebuilds the in-process DaprMQClient against
 * the merged config and closes the previous one. Throws InvalidConfigError (-> 400
 * INVALID_CONFIG) if a supplied field is empty or malformed.
 */
export function applyConfigOverride(input: ConfigOverrideInput): ConfigResponse {
  const next: DaprMQRuntimeConfig = { ...config };

  if (input.httpBaseUrl !== undefined) {
    if (typeof input.httpBaseUrl !== "string" || input.httpBaseUrl.trim() === "" || !isValidHttpUrl(input.httpBaseUrl)) {
      throw new InvalidConfigError("httpBaseUrl must be a non-empty, valid http(s) URL");
    }
    next.httpBaseUrl = input.httpBaseUrl;
  }

  if (input.grpcAddress !== undefined) {
    if (typeof input.grpcAddress !== "string" || !isValidGrpcAddress(input.grpcAddress)) {
      throw new InvalidConfigError("grpcAddress must be a non-empty host:port value with no scheme");
    }
    next.grpcAddress = input.grpcAddress;
  }

  if (input.queuePrefix !== undefined) {
    if (typeof input.queuePrefix !== "string" || input.queuePrefix.trim() === "") {
      throw new InvalidConfigError("queuePrefix must be a non-empty string");
    }
    next.queuePrefix = input.queuePrefix;
  }

  const newClient = buildClient(next);
  const oldClient = client;
  client = newClient;
  config = next;
  source = "override";
  oldClient.close();

  return getConfigResponse();
}

/** POST /reset: reverts config to env-derived defaults and rebuilds the client. */
export function resetConfig(): void {
  const next = readDefaultConfig();
  const newClient = buildClient(next);
  const oldClient = client;
  client = newClient;
  config = next;
  source = "default";
  oldClient.close();
}
