/** Command line of the TypeScript perf harness: the .NET harness's flags where they apply. */
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { PROFILES, SUITES, scaleOf } from "./profiles.js";
import { defaultEnvLabel } from "./records.js";

export const USAGE = `Usage: npm run perf -- [options]

  --suite pr|extreme     run every profile of that scale against one stack
  --profile NAME         one profile from sdks/testing/PERFORMANCE_TESTS.md (default: full)
  --api-replicas N       API server replicas behind nginx (default 1; extreme 3)
  --env-label NAME       series name on the charts (default local-<host>)
  --out DIR              results root; runs go to DIR/sdk-typescript (default <repo>/perf-results)
  --http URL --grpc URL  use an existing server instead of Testcontainers
  --gate                 exit 3 if a metric regresses (default: report only)
  --baseline-branch B    compare against recent runs from branch B (default main)
  --report               only regenerate DIR/report.html`;

export interface PerfOptions {
  profiles: string[];
  suite: string | null;
  apiReplicas: number;
  envLabel: string;
  outDir: string;
  http: string | null;
  grpc: string | null;
  gate: boolean;
  baselineBranch: string;
  reportOnly: boolean;
}

export function scaleFor(options: PerfOptions, profile: string): string {
  return options.suite ?? scaleOf(profile);
}

/** <repo>/perf-results, so every SDK's harness lands in one results root. */
export function defaultOutDir(): string {
  for (let dir = dirname(fileURLToPath(import.meta.url)); dir !== dirname(dir); dir = dirname(dir)) {
    if (existsSync(join(dir, "sdks", "testing", "PERFORMANCE_TESTS.md"))) {
      return join(dir, "perf-results");
    }
  }
  return resolve("perf-results");
}

export function parse(args: string[]): PerfOptions {
  // `npm run perf -- ...` can leave the separator in argv.
  args = args.filter((arg) => arg !== "--");
  const flags = new Map<string, string | null>();
  for (let i = 0; i < args.length; i++) {
    const name = args[i];
    if (name === "--gate" || name === "--report") {
      flags.set(name, null);
    } else if (name.startsWith("--") && i + 1 < args.length) {
      flags.set(name, args[++i]);
    } else {
      throw new Error(`Unknown or incomplete option '${name}'.\n\n${USAGE}`);
    }
  }

  const known = ["--suite", "--profile", "--api-replicas", "--env-label", "--out", "--http", "--grpc", "--gate", "--baseline-branch", "--report"];
  for (const name of flags.keys()) {
    if (!known.includes(name)) {
      throw new Error(`Unknown option '${name}'.\n\n${USAGE}`);
    }
  }

  const suite = flags.get("--suite") ?? null;
  const profile = flags.get("--profile") ?? null;
  const http = flags.get("--http") ?? null;
  const grpc = flags.get("--grpc") ?? null;
  const replicas = flags.has("--api-replicas") ? Number(flags.get("--api-replicas")) : null;

  if (suite !== null && !(suite in SUITES)) {
    throw new Error(`Unknown suite '${suite}'. Known: ${Object.keys(SUITES).join(", ")}.`);
  }
  if (suite !== null && profile !== null) {
    throw new Error("--profile can't be combined with --suite; the suite fixes the profiles.");
  }
  if (profile !== null && !PROFILES.includes(profile)) {
    throw new Error(`Unknown profile '${profile}'. Known: ${PROFILES.join(", ")}.`);
  }
  if ((http === null) !== (grpc === null)) {
    throw new Error("--http and --grpc must be given together.");
  }
  if (http !== null && replicas !== null) {
    throw new Error("--api-replicas configures the Testcontainers stack, so it can't be combined with --http/--grpc.");
  }
  if (replicas !== null && !(Number.isInteger(replicas) && replicas >= 1)) {
    throw new Error("--api-replicas must be >= 1.");
  }

  return {
    profiles: suite !== null ? SUITES[suite] : [profile ?? "full"],
    suite,
    apiReplicas: replicas ?? (suite === "extreme" ? 3 : 1),
    envLabel: flags.get("--env-label") ?? defaultEnvLabel(),
    outDir: resolve(flags.get("--out") ?? defaultOutDir()),
    http,
    grpc: grpc?.replace(/^https?:\/\//, "").replace(/\/+$/, "") ?? null,
    gate: flags.has("--gate"),
    baselineBranch: flags.get("--baseline-branch") ?? "main",
    reportOnly: flags.has("--report"),
  };
}
