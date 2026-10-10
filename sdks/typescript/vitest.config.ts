import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["tests/**/*.test.ts", "perf/tests/**/*.test.ts"],
    exclude: ["tests/integration/**", "node_modules/**"],
  },
});
