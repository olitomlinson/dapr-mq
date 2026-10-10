// Copies the wire contract into the package so the published tarball is self-contained.
// The source of truth stays server/src/DaprMQ.ApiServer/Protos/daprmq.proto; proto/daprmq.proto is gitignored.
import { copyFileSync } from "node:fs";

copyFileSync(new URL("../../../server/src/DaprMQ.ApiServer/Protos/daprmq.proto", import.meta.url), new URL("../proto/daprmq.proto", import.meta.url));
