namespace DaprMQ.PerfReport;

/// <summary>
/// report: regenerate report.html from every SDK's results.
/// check:  compare the given runs with recent baseline-branch runs (markdown to stdout and the
///         GitHub job summary); --gate exits 3 on a regression.
/// </summary>
public static class Cli
{
    public const string Usage = """
        Usage:
          report --results DIR [--out FILE]
              Write DIR/report.html (or FILE) from DIR/sdk-*/history.jsonl.
          merge --results DIR --from DIR2
              Add DIR2's runs to DIR: *.jsonl lines are unioned by runId (kept in time
              order), other files copied. Safe to re-run; never drops runs only DIR has.
          check --results DIR --runs ID[,ID...] [--baseline-branch main] [--gate]
              Compare runs with the median of recent baseline-branch runs of the same SDK,
              scenario, environment and API replica count. --gate exits 3 on a regression.
        """;

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(Usage);
        }

        var flags = Parse(args.Skip(1).ToArray());
        var results = flags.GetValueOrDefault("--results") ?? throw new ArgumentException($"--results is required.\n\n{Usage}");

        switch (args[0])
        {
            case "report":
                output.WriteLine($"Report: {HtmlReport.Write(results, flags.GetValueOrDefault("--out"))}");
                return 0;

            case "merge":
                var from = flags.GetValueOrDefault("--from") ?? throw new ArgumentException($"--from is required.\n\n{Usage}");
                output.WriteLine($"Merged {Merge(from, results)} new history lines from {from} into {results}");
                return 0;

            case "check":
                var runIds = (flags.GetValueOrDefault("--runs") ?? throw new ArgumentException($"--runs is required.\n\n{Usage}"))
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (markdown, regressed) = Check(results, runIds, flags.GetValueOrDefault("--baseline-branch") ?? "main");
                output.WriteLine(markdown);
                if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summaryPath)
                {
                    File.AppendAllText(summaryPath, markdown + "\n");
                }

                return regressed && flags.ContainsKey("--gate") ? 3 : 0;

            default:
                throw new ArgumentException($"Unknown command '{args[0]}'.\n\n{Usage}");
        }
    }

    public static (string Markdown, bool Regressed) Check(string results, IReadOnlyList<string> runIds, string baselineBranch)
    {
        var history = PerfResults.LoadHistory(results);
        var runs = runIds
            .Select(id => history.SingleOrDefault(r => RegressionCheck.Str(r, "runId") == id) ?? throw new ArgumentException($"Run '{id}' not found under {results}."))
            .ToList();

        var comparisons = runs.Select(r => RegressionCheck.Compare(r, history, baselineBranch)).ToList();
        var sdks = string.Join(", ", runs.Select(r => RegressionCheck.Str(r, "sdk", "name")).Distinct());
        var markdown = RegressionCheck.ToMarkdown(comparisons, baselineBranch, $"Perf ({sdks})");

        var failed = runs.Where(r => r["checks"]?["passed"]?.GetValue<bool>() == false).ToList();
        if (failed.Count > 0)
        {
            markdown += "\n" + string.Concat(failed.Select(r =>
                $"- ❌ `{RegressionCheck.Str(r, "scenario", "profile")}` failed its checks: {string.Join("; ", r["checks"]!["failures"]?.AsArray().Select(f => f!.GetValue<string>()) ?? [])}\n"));
        }

        return (markdown, comparisons.Any(c => c.Regressed));
    }

    /// <summary>
    /// Copies <paramref name="from"/> into <paramref name="into"/>: history files (*.jsonl) get the
    /// lines whose runId they don't have yet, re-sorted by timestamp; other files are copied over.
    /// Returns the number of history lines added.
    /// </summary>
    public static int Merge(string from, string into)
    {
        var added = 0;
        foreach (var source in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(into, Path.GetRelativePath(from, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!source.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                File.Copy(source, target, overwrite: true);
                continue;
            }

            static IEnumerable<string> Lines(string path) =>
                File.Exists(path) ? File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)) : [];
            static string RunId(string line) => System.Text.Json.Nodes.JsonNode.Parse(line)?["runId"]?.GetValue<string>() ?? line;

            var existing = Lines(target).ToList();
            var known = existing.Select(RunId).ToHashSet();
            var fresh = Lines(source).Where(l => known.Add(RunId(l))).ToList();
            added += fresh.Count;
            var merged = existing.Concat(fresh)
                .OrderBy(l => System.Text.Json.Nodes.JsonNode.Parse(l) is System.Text.Json.Nodes.JsonObject o ? PerfResults.Timestamp(o) : DateTimeOffset.MinValue)
                .ToList();
            File.WriteAllLines(target, merged);
        }

        return added;
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var flags = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--gate")
            {
                flags[args[i]] = null;
            }
            else if (args[i].StartsWith("--") && i + 1 < args.Length)
            {
                flags[args[i]] = args[++i];
            }
            else
            {
                throw new ArgumentException($"Unknown or incomplete option '{args[i]}'.\n\n{Usage}");
            }
        }

        return flags;
    }
}
