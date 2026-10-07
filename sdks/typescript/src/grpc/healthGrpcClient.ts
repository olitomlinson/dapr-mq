import * as grpc from "@grpc/grpc-js";
import * as protoLoader from "@grpc/proto-loader";
import { fileURLToPath } from "node:url";
import path from "node:path";

// Resolves the same from src/grpc and dist/grpc.
const PROTO_PATH = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../proto/health.proto");

/** One Watch stream: async-iterable statuses ("SERVING", "NOT_SERVING", ...), cancellable. */
export interface HealthWatchCall extends AsyncIterable<{ status: string }> {
  cancel(): void;
}

/** The slice of grpc.health.v1.Health the client uses (a seam for tests). */
export interface HealthGrpcClient {
  watch(service: string): HealthWatchCall;
  close(): void;
}

export function createHealthGrpcClient(address: string, credentials: grpc.ChannelCredentials): HealthGrpcClient {
  const definition = protoLoader.loadSync(PROTO_PATH, { keepCase: false, enums: String, defaults: true });
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const proto = grpc.loadPackageDefinition(definition) as any;
  const client = new proto.grpc.health.v1.Health(address, credentials);
  return {
    watch: (service) => client.watch({ service }) as HealthWatchCall,
    close: () => client.close(),
  };
}
