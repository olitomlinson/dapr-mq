using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace DaprMQ.Client.Tests;

/// <summary>The published package must carry what nuget.org and users need: README, licence, repository and symbols.</summary>
public class PackageTests : IDisposable
{
    private readonly string _outDir = Directory.CreateTempSubdirectory("daprmq-pack-").FullName;

    public void Dispose() => Directory.Delete(_outDir, recursive: true);

    [Fact]
    public void Package_HasReadmeLicenceRepositoryAndSymbols()
    {
        Pack();

        var nupkg = Directory.GetFiles(_outDir, "*.nupkg").Single();
        using var zip = ZipFile.OpenRead(nupkg);
        Assert.NotNull(zip.GetEntry("README.md"));

        var nuspec = XDocument.Load(zip.GetEntry("DaprMQ.Client.nuspec")!.Open()).Root!;
        var ns = nuspec.Name.Namespace;
        var metadata = nuspec.Element(ns + "metadata")!;
        Assert.Equal("README.md", metadata.Element(ns + "readme")?.Value);
        Assert.Equal("Apache-2.0", metadata.Element(ns + "license")?.Value);
        Assert.Equal("https://github.com/olitomlinson/dapr-mq", metadata.Element(ns + "repository")?.Attribute("url")?.Value);
        Assert.Equal("https://github.com/olitomlinson/dapr-mq", metadata.Element(ns + "projectUrl")?.Value);
        Assert.NotEqual("Package Description", metadata.Element(ns + "description")?.Value);

        Assert.Single(Directory.GetFiles(_outDir, "*.snupkg"));
    }

    // Packs the build the test project already compiled, so it doesn't race the test run's own build.
    private void Pack()
    {
        var csproj = FindRepoFile("sdks/dotnet/src/DaprMQ.Client/DaprMQ.Client.csproj");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var psi = new ProcessStartInfo("dotnet", ["pack", csproj, "--no-build", "-c", configuration, "-o", _outDir])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }
}
