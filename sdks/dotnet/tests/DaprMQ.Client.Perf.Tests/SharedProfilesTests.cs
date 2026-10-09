using System.Text.Json.Nodes;
using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

/// <summary>
/// sdks/testing/perf/profiles.json is what every other SDK's harness checks its profile keys
/// against, so it must say exactly what this reference implementation records.
/// </summary>
public class SharedProfilesTests
{
    private static JsonObject Shared()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "sdks", "testing", "perf", "profiles.json");
            if (File.Exists(path))
            {
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            }
        }

        throw new FileNotFoundException("sdks/testing/perf/profiles.json");
    }

    [Fact]
    public void EveryProfile_MatchesTheSharedFile()
    {
        var profiles = Shared()["profiles"]!.AsObject();
        var all = PerfOptions.Suites.Values.SelectMany(p => p).ToList();

        Assert.Equal(all.Order(), profiles.Select(p => p.Key).Order());
        foreach (var profile in all)
        {
            var options = PerfOptions.Parse(["--profile", profile]);
            var (id, name, key) = options.Load is { } load ? (load.Id, load.Scenario, load.Key)
                : options.QueueDrain is { } queue ? ("P-05", "queue-drain", queue.Key)
                : ("P-04", "session-drain", options.Scenario.Key);

            var expected = profiles[profile]!;
            Assert.Equal(expected["id"]!.GetValue<string>(), id);
            Assert.Equal(expected["name"]!.GetValue<string>(), name);
            Assert.Equal(expected["scale"]!.GetValue<string>(), options.Scale);
            Assert.Equal(expected["key"]!.GetValue<string>(), key);
        }
    }

    [Fact]
    public void Suites_MatchTheSharedFile()
    {
        var suites = Shared()["suites"]!.AsObject();

        Assert.Equal(PerfOptions.Suites.Keys.Order(), suites.Select(s => s.Key).Order());
        foreach (var (suite, profiles) in PerfOptions.Suites)
        {
            Assert.Equal(suites[suite]!.AsArray().Select(p => p!.GetValue<string>()), profiles);
        }
    }
}
