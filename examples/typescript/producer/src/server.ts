import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { LANGUAGE, ROLE } from "./constants.js";
import { applyConfigOverride, getConfigResponse, InvalidConfigError, resetConfig, type ConfigOverrideInput } from "./config.js";
import { log } from "./logger.js";
import { SCENARIOS } from "./scenarios.js";

let scenarioInProgress = false;

function sendJson(res: ServerResponse, status: number, body: unknown): void {
  const text = JSON.stringify(body);
  res.writeHead(status, { "content-type": "application/json" });
  res.end(text);
}

function sendError(res: ServerResponse, status: number, code: string, message: string): void {
  sendJson(res, status, { error: { code, message } });
}

async function readBody(req: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) {
    chunks.push(chunk as Buffer);
  }
  return Buffer.concat(chunks).toString("utf8");
}

async function handleGetConfig(_req: IncomingMessage, res: ServerResponse): Promise<void> {
  sendJson(res, 200, getConfigResponse());
}

async function handlePutConfig(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const bodyText = await readBody(req);
  let parsed: unknown = {};
  if (bodyText.trim() !== "") {
    try {
      parsed = JSON.parse(bodyText);
    } catch {
      sendError(res, 400, "INVALID_CONFIG", "Request body must be valid JSON");
      return;
    }
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    sendError(res, 400, "INVALID_CONFIG", "Request body must be a JSON object");
    return;
  }

  try {
    const updated = applyConfigOverride(parsed as ConfigOverrideInput);
    sendJson(res, 200, updated);
  } catch (err) {
    if (err instanceof InvalidConfigError) {
      sendError(res, 400, "INVALID_CONFIG", err.message);
      return;
    }
    throw err;
  }
}

async function handleReset(_req: IncomingMessage, res: ServerResponse): Promise<void> {
  resetConfig();
  sendJson(res, 200, { success: true, message: "State reset to defaults" });
}

async function handleGetScenarios(_req: IncomingMessage, res: ServerResponse): Promise<void> {
  sendJson(
    res,
    200,
    SCENARIOS.map((s) => ({ name: s.name, role: s.role, description: s.description })),
  );
}

async function handleRunScenario(name: string, res: ServerResponse): Promise<void> {
  const scenario = SCENARIOS.find((s) => s.name === name);
  if (!scenario) {
    sendError(res, 404, "UNKNOWN_SCENARIO", `Unknown scenario: ${name}`);
    return;
  }

  if (scenarioInProgress) {
    sendError(res, 409, "SCENARIO_IN_PROGRESS", "A scenario run is already in progress on this pod");
    return;
  }

  scenarioInProgress = true;
  const startedAt = new Date().toISOString();
  try {
    const result = await scenario.run();
    const finishedAt = new Date().toISOString();
    sendJson(res, 200, {
      scenario: name,
      role: ROLE,
      queueId: result.queueId,
      startedAt,
      finishedAt,
      steps: result.steps,
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    log("ERROR", `Scenario ${name} failed: ${message}`);
    sendError(res, 502, "UPSTREAM_ERROR", message);
  } finally {
    scenarioInProgress = false;
  }
}

export function createControlServer(): Server {
  return createServer((req, res) => {
    void (async () => {
      try {
        const method = req.method ?? "GET";
        const url = new URL(req.url ?? "/", "http://localhost");
        const path = url.pathname;

        if (method === "GET" && path === "/health") {
          sendJson(res, 200, { status: "ok", language: LANGUAGE, role: ROLE });
          return;
        }

        if (method === "GET" && path === "/config") {
          await handleGetConfig(req, res);
          return;
        }

        if (method === "PUT" && path === "/config") {
          await handlePutConfig(req, res);
          return;
        }

        if (method === "POST" && path === "/reset") {
          await handleReset(req, res);
          return;
        }

        if (method === "GET" && path === "/scenarios") {
          await handleGetScenarios(req, res);
          return;
        }

        const scenarioRunMatch = /^\/scenarios\/([^/]+)\/run$/.exec(path);
        if (method === "POST" && scenarioRunMatch) {
          await handleRunScenario(decodeURIComponent(scenarioRunMatch[1]), res);
          return;
        }

        res.writeHead(404, { "content-type": "text/plain" });
        res.end("Not Found");
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        log("ERROR", `Unhandled error handling ${req.method} ${req.url}: ${message}`);
        sendError(res, 502, "UPSTREAM_ERROR", message);
      }
    })();
  });
}
