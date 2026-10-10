// The published tarball must work on its own: no path may reach outside the package (e.g. into server/).
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

const sdkDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

describe("npm package", () => {
  let workDir: string;
  let packageDir: string;

  beforeAll(() => {
    workDir = mkdtempSync(path.join(tmpdir(), "daprmq-pack-"));
    const out = execFileSync("npm", ["pack", "--json", "--pack-destination", workDir], { cwd: sdkDir, encoding: "utf8" });
    const [{ filename }] = JSON.parse(out.slice(out.indexOf("["))) as { filename: string }[];
    execFileSync("tar", ["-xzf", path.join(workDir, filename), "-C", workDir]);
    packageDir = path.join(workDir, "package");
    symlinkSync(path.join(sdkDir, "node_modules"), path.join(packageDir, "node_modules"));
  }, 120_000);

  afterAll(() => {
    if (workDir) rmSync(workDir, { recursive: true, force: true });
  });

  it("creates a gRPC client from the unpacked tarball alone", async () => {
    const { DaprMQClient } = await import(path.join(packageDir, "dist/index.js"));
    const client = new DaprMQClient({ httpBaseUrl: "http://localhost:1", grpcAddress: "localhost:1" });
    client.close();
  });

  // Scoped packages default to private, and npm rejects provenance without a matching repository.
  it("publishes publicly with provenance metadata", () => {
    const pkg = JSON.parse(readFileSync(path.join(packageDir, "package.json"), "utf8"));
    expect(pkg.publishConfig?.access).toBe("public");
    expect(pkg.repository).toEqual({
      type: "git",
      url: "git+https://github.com/olitomlinson/dapr-mq.git",
      directory: "sdks/typescript",
    });
  });

  it("ships the readme and licence", () => {
    expect(existsSync(path.join(packageDir, "README.md"))).toBe(true);
    expect(existsSync(path.join(packageDir, "LICENSE"))).toBe(true);
  });
});
