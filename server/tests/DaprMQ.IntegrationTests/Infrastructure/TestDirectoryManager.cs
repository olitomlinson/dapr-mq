using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DaprMQ.IntegrationTests.Infrastructure;

public static class TestDirectoryManager
{
    // Use the system temp path as the base for cross-platform compatibility
    private static readonly string BasePath = Path.GetTempPath();

    /// <summary>
    /// Creates a new test directory.
    /// </summary>
    /// <param name="prefix">Any optional prefix value to set on the directory name.</param>
    /// <returns></returns>
    public static string CreateTestDirectory(string prefix)
    {
        var folderName = $"{prefix}-{Guid.NewGuid():N}";
        var directoryPath = Path.Combine(BasePath, folderName);

        Directory.CreateDirectory(directoryPath);

        // For Linux/Unix: Ensure directory has appropriate permissions (777).
        // This is crucial for Docker volume mounts where the container user might not match the host user.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"-R 777 \"{directoryPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(processStartInfo);
                process?.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to set permissions on {directoryPath}: {ex.Message}");
            }
        }

        return directoryPath;
    }

    /// <summary>
    /// Attempts to delete the directory and all its contents.
    /// </summary>
    public static void CleanUpDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return;

        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, true);
            }
        }
        catch (Exception ex)
        {
            // Log warning but don't crash; typical issue is file locking or racing containers
            Console.WriteLine($"Warning: Failed to clean up directory {directoryPath}: {ex.Message}");
        }
    }
}