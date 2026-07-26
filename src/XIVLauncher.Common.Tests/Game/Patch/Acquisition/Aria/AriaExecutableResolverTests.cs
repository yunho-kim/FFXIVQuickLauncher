using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.Patch.Acquisition.Aria;

namespace XIVLauncher.Common.Tests.Game.Patch.Acquisition.Aria;

[TestClass]
public class AriaExecutableResolverTests
{
    [TestMethod]
    public void WindowsUsesBundledExecutable()
    {
        var result = AriaExecutableResolver.Resolve(
            OSPlatform.Windows,
            "/app/Resources",
            null,
            _ => false);

        Assert.AreEqual(Path.Combine("/app/Resources", "aria2c-xl.exe"), result);
    }

    [TestMethod]
    public void MacOSPrefersBundledExecutable()
    {
        var bundledPath = Path.Combine("/app/Resources", "aria2c");

        var result = AriaExecutableResolver.Resolve(
            OSPlatform.OSX,
            "/app/Resources",
            "/custom/bin",
            path => path == bundledPath);

        Assert.AreEqual(bundledPath, result);
    }

    [TestMethod]
    public void MacOSFindsExecutableOnPath()
    {
        var expected = Path.Combine("/custom/bin", "aria2c");

        var result = AriaExecutableResolver.Resolve(
            OSPlatform.OSX,
            "/app/Resources",
            $"/first/bin{Path.PathSeparator}/custom/bin",
            path => path == expected);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void MacOSFindsHomebrewExecutableWhenFinderPathIsMinimal()
    {
        const string expected = "/opt/homebrew/bin/aria2c";

        var result = AriaExecutableResolver.Resolve(
            OSPlatform.OSX,
            "/app/Resources",
            "/usr/bin:/bin:/usr/sbin:/sbin",
            path => path == expected);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void MacOSThrowsHelpfulErrorWhenExecutableIsMissing()
    {
        var exception = Assert.ThrowsException<FileNotFoundException>(() =>
            AriaExecutableResolver.Resolve(
                OSPlatform.OSX,
                "/app/Resources",
                "/usr/bin:/bin",
                _ => false,
                Array.Empty<string>()));

        StringAssert.Contains(exception.Message, "Homebrew or MacPorts");
    }

    [TestMethod]
    public void LinuxFindsExecutableOnPath()
    {
        var expected = Path.Combine("/usr/bin", "aria2c");

        var result = AriaExecutableResolver.Resolve(
            OSPlatform.Linux,
            "/app/Resources",
            "/usr/local/bin:/usr/bin",
            path => path == expected,
            Array.Empty<string>());

        Assert.AreEqual(expected, result);
    }
}
