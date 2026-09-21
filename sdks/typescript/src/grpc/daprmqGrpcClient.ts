import * as grpc from "@grpc/grpc-js";
import * as protoLoader from "@grpc/proto-loader";
import { fileURLToPath } from "node:url";
import path from "node:path";

// Single source of truth for the wire contract stays server/src/DaprMQ.ApiServer/Protos/daprmq.proto
// (mirrors how sdks/dotnet/src/DaprMQ.Client references the same file rather than duplicating it).
const PROTO_PATH = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../../../server/src/DaprMQ.ApiServer/Protos/daprmq.proto",
);

export interface ConsumeSessionStartMessage {
  queueId: string;
  sessionId?: string;
  leaseSeconds: number;
  prefetchCount: number;
  sessionIdleTimeoutSeconds?: number;
}

export interface ConsumeSessionRequestMessage {
  start?: ConsumeSessionStartMessage;
  ack?: { lockId: string };
  deadLetter?: { lockId: string };
}

export interface SessionAssignedMessage {
  sessionId: string;
  leaseExpiresAt: number;
}

export interface SessionDeliveredMessage {
  lockId: string;
  itemJson: string;
  priority: number;
  lockExpiresAt: number;
}

export interface SessionErrorMessage {
  errorCode: string;
  message: string;
}

export interface SessionLostMessage {
  message: string;
}

export interface SessionDrainedMessage {
  sessionId: string;
}

export interface ConsumeSessionResponseMessage {
  payload: "sessionAssigned" | "delivered" | "error" | "sessionLost" | "sessionDrained";
  sessionAssigned?: SessionAssignedMessage;
  delivered?: SessionDeliveredMessage;
  error?: SessionErrorMessage;
  sessionLost?: SessionLostMessage;
  sessionDrained?: SessionDrainedMessage;
}

export interface DaprMQGrpcClient extends grpc.Client {
  consumeSession(): grpc.ClientDuplexStream<ConsumeSessionRequestMessage, ConsumeSessionResponseMessage>;
}

interface DaprMQGrpcPackage {
  daprmq: {
    DaprMQ: new (address: string, credentials: grpc.ChannelCredentials) => DaprMQGrpcClient;
  };
}

let cachedPackage: DaprMQGrpcPackage | undefined;

function loadPackage(): DaprMQGrpcPackage {
  if (!cachedPackage) {
    const packageDefinition = protoLoader.loadSync(PROTO_PATH, {
      keepCase: false,
      longs: Number,
      enums: String,
      defaults: true,
      oneofs: true,
    });
    cachedPackage = grpc.loadPackageDefinition(packageDefinition) as unknown as DaprMQGrpcPackage;
  }
  return cachedPackage;
}

export function createDaprMQGrpcClient(address: string, credentials: grpc.ChannelCredentials): DaprMQGrpcClient {
  const { daprmq } = loadPackage();
  return new daprmq.DaprMQ(address, credentials);
}
