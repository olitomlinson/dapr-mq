using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DaprMQ.Client.Perf;

public sealed record RunEnvironment(string Label, string? GitSha, string? GitBranch, bool GitDirty, string Os, int CpuCount, string Dotnet, string Server)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static RunEnvironment Capture(string label, string server) => new(
        label,
        Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Git("rev-parse HEAD"),
        Environment.GetEnvironmentVariable("GITHUB_HEAD_REF") is { Length: > 0 } prBranch
            ? prBranch
            : Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ?? Git("rev-parse --abbrev-ref HEAD"),
        !string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no")),
        RuntimeInformation.OSDescription,
        Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription,
        server);

    private static string? Git(string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
