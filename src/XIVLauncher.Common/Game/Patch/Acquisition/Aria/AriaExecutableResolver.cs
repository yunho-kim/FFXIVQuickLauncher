using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace XIVLauncher.Common.Game.Patch.Acquisition.Aria;

public static class AriaExecutableResolver
{
    private static readonly string[] MacOSSearchDirectories =
    {
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/opt/local/bin",
    };

    public static string Resolve()
    {
        OSPlatform platform;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            platform = OSPlatform.Windows;
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            platform = OSPlatform.OSX;
        else
            platform = OSPlatform.Linux;

        return Resolve(
            platform,
            Paths.ResourcesPath,
            Environment.GetEnvironmentVariable("PATH"),
            File.Exists);
    }

    public static string Resolve(
        OSPlatform platform,
        string resourcesPath,
        string? pathEnvironment,
        Func<string, bool> fileExists,
        IEnumerable<string>? macOSSearchDirectories = null)
    {
        if (platform == OSPlatform.Windows)
            return Path.Combine(resourcesPath, "aria2c-xl.exe");

        var bundledPath = Path.Combine(resourcesPath, "aria2c");
        if (fileExists(bundledPath))
            return bundledPath;

        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, "aria2c");
                if (fileExists(candidate))
                    return candidate;
            }
        }

        if (platform == OSPlatform.OSX)
        {
            foreach (var directory in macOSSearchDirectories ?? MacOSSearchDirectories)
            {
                var candidate = Path.Combine(directory, "aria2c");
                if (fileExists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            platform == OSPlatform.OSX
                ? "aria2c was not found. Bundle it in the app's Resources directory or install it with Homebrew or MacPorts."
                : "aria2c was not found. Install aria2 and make sure aria2c is available on PATH.",
            "aria2c");
    }
}
