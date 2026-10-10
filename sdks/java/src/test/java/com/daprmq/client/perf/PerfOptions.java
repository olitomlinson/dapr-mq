package com.daprmq.client.perf;

import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Command line of the Java perf harness: the .NET harness's flags where they apply. */
public record PerfOptions(List<String> profiles, String suite, int apiReplicas, String envLabel, Path outDir, String http, String grpc,
                          boolean gate, String baselineBranch, boolean reportOnly) {

    public static final String USAGE = """
            Usage: mvn -q -Pperf test-compile exec:exec -Dperf.args="[options]"

              --suite pr|extreme     run every profile of that scale against one stack
              --profile NAME         one profile from sdks/testing/PERFORMANCE_TESTS.md (default: full)
              --api-replicas N       API server replicas behind nginx (default 1; extreme 3)
              --env-label NAME       series name on the charts (default local-<host>)
              --out DIR              results root; runs go to DIR/sdk-java (default <repo>/perf-results)
              --http URL --grpc URL  use an existing server instead of Testcontainers
              --gate                 exit 3 if a metric regresses (default: report only)
              --baseline-branch B    compare against recent runs from branch B (default main)
              --report               only regenerate DIR/report.html""";

    private static final Set<String> KNOWN = Set.of(
            "--suite", "--profile", "--api-replicas", "--env-label", "--out", "--http", "--grpc", "--gate", "--baseline-branch", "--report");

    public String scale(String profile) {
        return suite != null ? suite : Profiles.scaleOf(profile);
    }

    /** {@code <repo>/perf-results}, so every SDK's harness lands in one results root. */
    public static Path defaultOutDir() {
        for (Path dir = Paths.get("").toAbsolutePath(); dir != null; dir = dir.getParent()) {
            if (Files.exists(dir.resolve("sdks/testing/PERFORMANCE_TESTS.md"))) {
                return dir.resolve("perf-results");
            }
        }
        return Paths.get("perf-results").toAbsolutePath();
    }

    public static PerfOptions parse(String[] args) {
        Map<String, String> flags = new LinkedHashMap<>();
        for (int i = 0; i < args.length; i++) {
            String name = args[i];
            if (name.equals("--gate") || name.equals("--report")) {
                flags.put(name, null);
            } else if (name.startsWith("--") && i + 1 < args.length) {
                flags.put(name, args[++i]);
            } else {
                throw new IllegalArgumentException("Unknown or incomplete option '" + name + "'.\n\n" + USAGE);
            }
        }
        for (String name : flags.keySet()) {
            if (!KNOWN.contains(name)) {
                throw new IllegalArgumentException("Unknown option '" + name + "'.\n\n" + USAGE);
            }
        }

        String suite = flags.get("--suite");
        String profile = flags.get("--profile");
        String http = flags.get("--http");
        String grpc = flags.get("--grpc");
        Integer replicas = flags.containsKey("--api-replicas") ? Integer.valueOf(flags.get("--api-replicas")) : null;

        if (suite != null && !Profiles.SUITES.containsKey(suite)) {
            throw new IllegalArgumentException("Unknown suite '" + suite + "'. Known: " + String.join(", ", Profiles.SUITES.keySet()) + ".");
        }
        if (suite != null && profile != null) {
            throw new IllegalArgumentException("--profile can't be combined with --suite; the suite fixes the profiles.");
        }
        if (profile != null && !Profiles.all().contains(profile)) {
            throw new IllegalArgumentException("Unknown profile '" + profile + "'. Known: " + String.join(", ", Profiles.all()) + ".");
        }
        if ((http == null) != (grpc == null)) {
            throw new IllegalArgumentException("--http and --grpc must be given together.");
        }
        if (http != null && replicas != null) {
            throw new IllegalArgumentException("--api-replicas configures the Testcontainers stack, so it can't be combined with --http/--grpc.");
        }
        if (replicas != null && replicas < 1) {
            throw new IllegalArgumentException("--api-replicas must be >= 1.");
        }

        return new PerfOptions(
                suite != null ? Profiles.SUITES.get(suite) : List.of(profile != null ? profile : "full"),
                suite,
                replicas != null ? replicas : ("extreme".equals(suite) ? 3 : 1),
                flags.containsKey("--env-label") ? flags.get("--env-label") : Records.defaultEnvLabel(),
                flags.containsKey("--out") ? Paths.get(flags.get("--out")).toAbsolutePath() : defaultOutDir(),
                http,
                grpc == null ? null : grpc.replaceFirst("^https?://", "").replaceFirst("/+$", ""),
                flags.containsKey("--gate"),
                flags.getOrDefault("--baseline-branch", "main"),
                flags.containsKey("--report"));
    }
}
