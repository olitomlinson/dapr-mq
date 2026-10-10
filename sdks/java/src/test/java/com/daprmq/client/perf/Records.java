package com.daprmq.client.perf;

import com.daprmq.client.perf.Profiles.LoadParams;
import com.daprmq.client.perf.Profiles.QueueDrainParams;
import com.daprmq.client.perf.Profiles.SessionDrainParams;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/**
 * Schema-2 run records (sdks/testing/perf/result.schema.json), built exactly as
 * sdks/dotnet/perf/DaprMQ.Client.Perf/RunRecords.cs builds them, and saved in the shared layout:
 * {@code <out>/sdk-java/runs/<runId>.json} in full, {@code <out>/sdk-java/history.jsonl} without the timeline.
 */
public final class Records {
    public static final int SCHEMA_VERSION = 2;
    public static final String SDK = "java";
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final DateTimeFormatter RUN_ID_TIME = DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss'Z'").withZone(ZoneOffset.UTC);

    private Records() {
    }

    public record RunEnvironment(String label, String gitSha, String gitBranch, boolean gitDirty, String os, int cpuCount, String runtime, String server) {
        public static RunEnvironment capture(String label, String server) {
            String branch = firstNonEmpty(System.getenv("GITHUB_HEAD_REF"), System.getenv("GITHUB_REF_NAME"));
            String dirty = git("status", "--porcelain", "--untracked-files=no");
            return new RunEnvironment(
                    label,
                    firstNonEmpty(System.getenv("GITHUB_SHA"), git("rev-parse", "HEAD")),
                    branch != null ? branch : git("rev-parse", "--abbrev-ref", "HEAD"),
                    dirty != null && !dirty.isEmpty(),
                    System.getProperty("os.name") + " " + System.getProperty("os.version"),
                    Runtime.getRuntime().availableProcessors(),
                    System.getProperty("java.vm.name") + " " + System.getProperty("java.version"),
                    server);
        }
    }

    /** Everything about a run that isn't its scenario or result. */
    public record RunContext(Instant timestamp, RunEnvironment environment, String scale, int apiReplicas, Integer schedulerReplicas, String sdkVersion) {
    }

    public record LoadResult(List<Map<String, Object>> steps, List<Map<String, List<Object>>> timelines, boolean drained, List<String> sampleErrors) {
    }

    private static String firstNonEmpty(String a, String b) {
        return a != null && !a.isEmpty() ? a : (b != null && !b.isEmpty() ? b : null);
    }

    private static String git(String... args) {
        List<String> command = new ArrayList<>(List.of("git"));
        command.addAll(List.of(args));
        try {
            Process process = new ProcessBuilder(command).redirectError(ProcessBuilder.Redirect.DISCARD).start();
            String out = new String(process.getInputStream().readAllBytes(), StandardCharsets.UTF_8).trim();
            return process.waitFor(10, TimeUnit.SECONDS) && process.exitValue() == 0 ? out : null;
        } catch (IOException e) {
            return null;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return null;
        }
    }

    public static String defaultEnvLabel() {
        try {
            return "local-" + InetAddress.getLocalHost().getHostName().split("\\.")[0].toLowerCase();
        } catch (IOException e) {
            return "local";
        }
    }

    public static String sdkVersion() {
        return com.daprmq.client.DaprMQClient.class.getPackage().getImplementationVersion();
    }

    private static Map<String, Object> envelope(RunContext context, String profile, String id, String name, String key,
                                                Map<String, Object> params, Map<String, Object> metrics, List<String> failures) {
        RunEnvironment env = context.environment();
        Map<String, Object> run = new LinkedHashMap<>();
        run.put("schemaVersion", SCHEMA_VERSION);
        run.put("runId", RUN_ID_TIME.format(context.timestamp()) + "_" + SDK + "_" + env.label() + "_" + profile);
        run.put("timestampUtc", context.timestamp().toString());

        Map<String, Object> sdk = new LinkedHashMap<>();
        sdk.put("name", SDK);
        sdk.put("version", context.sdkVersion());
        sdk.put("runtime", env.runtime());
        run.put("sdk", sdk);

        Map<String, Object> environment = new LinkedHashMap<>();
        environment.put("label", env.label());
        environment.put("gitSha", env.gitSha());
        environment.put("gitBranch", env.gitBranch());
        environment.put("gitDirty", env.gitDirty());
        environment.put("os", env.os());
        environment.put("cpuCount", env.cpuCount());
        run.put("environment", environment);

        Map<String, Object> topology = new LinkedHashMap<>();
        topology.put("apiReplicas", context.apiReplicas());
        topology.put("schedulerReplicas", context.schedulerReplicas());
        topology.put("loadBalancer", context.apiReplicas() > 1);
        topology.put("server", env.server());
        run.put("topology", topology);

        run.put("scale", context.scale());
        Map<String, Object> scenario = new LinkedHashMap<>();
        scenario.put("id", id);
        scenario.put("name", name);
        scenario.put("profile", profile);
        scenario.put("key", key);
        scenario.put("params", params);
        run.put("scenario", scenario);
        run.put("metrics", metrics);

        Map<String, Object> checks = new LinkedHashMap<>();
        checks.put("passed", failures.isEmpty());
        checks.put("failures", failures);
        run.put("checks", checks);
        return run;
    }

    private static double num(Map<String, Object> map, String key) {
        return ((Number) map.get(key)).doubleValue();
    }

    public static Map<String, Object> load(RunContext context, String profile, LoadParams load, LoadResult result) {
        // A ramp's headline is its best step; ops/messages/errors cover every step.
        Map<String, Object> best = result.steps().stream().max((a, b) -> Double.compare(num(a, "messagesPerSecond"), num(b, "messagesPerSecond"))).orElseThrow();
        int errors = result.steps().stream().mapToInt(s -> (int) num(s, "errors")).sum();
        Map<String, Object> metrics = new LinkedHashMap<>();
        metrics.put("opsPerSecond", best.get("opsPerSecond"));
        metrics.put("messagesPerSecond", best.get("messagesPerSecond"));
        metrics.put("latencyMs", best.get("latencyMs"));
        metrics.put("ops", result.steps().stream().mapToInt(s -> (int) num(s, "ops")).sum());
        metrics.put("messages", result.steps().stream().mapToInt(s -> (int) num(s, "messages")).sum());
        metrics.put("errors", errors);

        List<String> failures = new ArrayList<>();
        if (errors > 0) {
            failures.add(errors + " errors" + (result.sampleErrors().isEmpty() ? "" : " (first: " + result.sampleErrors().get(0) + ")"));
        }
        if (result.drained()) {
            failures.add("a queue drained before the window ended: raise seedPerQueue");
        }

        Map<String, Object> run = envelope(context, profile, load.id(), load.scenario(), load.key(), load.params(), metrics, failures);
        run.put("steps", result.steps());
        Map<String, Object> series = new LinkedHashMap<>();
        for (String name : List.of("opsPerSecond", "messagesPerSecond", "latencyP50Ms", "latencyP95Ms", "latencyP99Ms", "errors", "concurrency")) {
            List<Object> values = new ArrayList<>();
            result.timelines().forEach(t -> values.addAll(t.get(name)));
            series.put(name, values);
        }
        run.put("timeline", timeline(1000, series));
        return run;
    }

    public static Map<String, Object> sessionDrain(RunContext context, String profile, SessionDrainParams scenario, Map<String, Object> result) {
        Map<String, Object> metrics = new LinkedHashMap<>(result);
        Object busy = metrics.remove("busySlotsTimeline");
        Object bucketMs = metrics.remove("timelineBucketMs");

        List<String> failures = new ArrayList<>();
        if (num(metrics, "missing") > 0) {
            failures.add(metrics.get("missing") + " messages missing");
        }
        if (num(metrics, "fifoViolations") > 0) {
            failures.add(metrics.get("fifoViolations") + " FIFO violations");
        }

        Map<String, Object> run = envelope(context, profile, "P-04", "session-drain", scenario.key(), scenario.params(), metrics, failures);
        run.put("timeline", timeline(bucketMs, Map.of("busySlots", busy)));
        return run;
    }

    public static Map<String, Object> queueDrain(RunContext context, String profile, QueueDrainParams scenario, Map<String, Object> result) {
        Map<String, Object> metrics = new LinkedHashMap<>(result);
        Object perSecond = metrics.remove("messagesPerSecondTimeline");
        Object busy = metrics.remove("busyHandlersTimeline");

        List<String> failures = new ArrayList<>();
        if (num(metrics, "missing") > 0) {
            failures.add(metrics.get("missing") + " messages missing");
        }
        // Above a window of 1, handlers legitimately start messages out of queue order.
        if (scenario.strictOrder() && num(metrics, "orderViolations") > 0) {
            failures.add(metrics.get("orderViolations") + " order violations");
        }

        Map<String, Object> run = envelope(context, profile, "P-05", "queue-drain", scenario.key(), scenario.params(), metrics, failures);
        Map<String, Object> series = new LinkedHashMap<>();
        series.put("messagesPerSecond", perSecond);
        series.put("busyHandlers", busy);
        run.put("timeline", timeline(1000, series));
        return run;
    }

    private static Map<String, Object> timeline(Object bucketMs, Map<String, Object> series) {
        Map<String, Object> timeline = new LinkedHashMap<>();
        timeline.put("bucketMs", bucketMs);
        timeline.put("series", series);
        return timeline;
    }

    public static Path save(Path root, Map<String, Object> run) {
        try {
            Path sdkDir = root.resolve("sdk-" + SDK);
            Path runsDir = sdkDir.resolve("runs");
            Files.createDirectories(runsDir);

            Path runPath = runsDir.resolve(run.get("runId") + ".json");
            Files.writeString(runPath, JSON.copy().enable(SerializationFeature.INDENT_OUTPUT).writeValueAsString(run));

            Map<String, Object> summary = new LinkedHashMap<>(run);
            summary.remove("timeline");
            Files.writeString(sdkDir.resolve("history.jsonl"), JSON.writeValueAsString(summary) + "\n",
                    StandardOpenOption.CREATE, StandardOpenOption.APPEND);
            return runPath;
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }
}
