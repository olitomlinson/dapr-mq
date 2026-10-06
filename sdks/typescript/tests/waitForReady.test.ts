// waitForReady (sdks/testing/RETRIES_AND_READINESS.md): grpc.health.v1 Watch until SERVING.
import * as grpc from "@grpc/grpc-js";
import { describe, expect, it } from "vitest";
import { DaprMQClient } from "../src/client.js";
import type { DaprMQGrpcClient } from "../src/grpc/daprmqGrpcClient.js";
import type { HealthGrpcClient, HealthWatchCall } from "../src/grpc/healthGrpcClient.js";

type Script = string[] | { hang: string[] } | grpc.status;

/** Each watch call plays the next scripted stream (the last one repeats). */
function fakeHealth(...scripts: Script[]) {
  const services: string[] = [];
  const health: HealthGrpcClient = {
    watch(service: string): HealthWatchCall {
      services.push(service);
      const script = scripts.length > 1 ? scripts.shift()! : scripts[0];
      let cancelled = false;
      return {
        cancel: () => {
          cancelled = true;
        },
        async *[Symbol.asyncIterator]() {
          if (typeof script === "number") {
            throw Object.assign(new Error("rpc failed"), { code: script });
          }
          const statuses = Array.isArray(script) ? script : script.hang;
          for (const status of statuses) {
            yield { status };
          }
          if (!Array.isArray(script)) {
            while (!cancelled) {
              await new Promise((r) => setTimeout(r, 10));
            }
          }
        },
      };
    },
    close: () => {},
  };
  return { health, services };
}

const client = (health: HealthGrpcClient) =>
  new DaprMQClient({ httpBaseUrl: "http://localhost:5000", grpcClient: {} as DaprMQGrpcClient, healthClient: health });

describe("waitForReady", () => {
  it("returns on SERVING, watching daprmq.DaprMQ.operations by default", async () => {
    const { health, services } = fakeHealth(["NOT_SERVING", "SERVING"]);

    await client(health).waitForReady();

    expect(services).toEqual(["daprmq.DaprMQ.operations"]);
  });

  it("can watch another service", async () => {
    const { health, services } = fakeHealth(["SERVING"]);

    await client(health).waitForReady({ service: "daprmq.DaprMQ" });

    expect(services).toEqual(["daprmq.DaprMQ"]);
  });

  it("reconnects while the server is unavailable", async () => {
    const { health, services } = fakeHealth(grpc.status.UNAVAILABLE, ["SERVING"]);

    await client(health).waitForReady();

    expect(services).toHaveLength(2);
  });

  it("reconnects when the stream ends before SERVING", async () => {
    const { health, services } = fakeHealth(["NOT_SERVING"], ["SERVING"]);

    await client(health).waitForReady();

    expect(services).toHaveLength(2);
  });

  it("fails as not supported on UNIMPLEMENTED", async () => {
    await expect(client(fakeHealth(grpc.status.UNIMPLEMENTED).health).waitForReady()).rejects.toThrow(/does not expose the gRPC health service/);
  });

  it("is bounded by the caller's signal", async () => {
    const { health } = fakeHealth({ hang: ["NOT_SERVING"] });

    await expect(client(health).waitForReady({ signal: AbortSignal.timeout(100) })).rejects.toThrow();
  });
});
