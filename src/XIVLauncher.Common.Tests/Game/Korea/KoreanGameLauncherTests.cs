using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.Korea;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Tests.Game.Korea;

[TestClass]
public sealed class KoreanGameLauncherTests
{
    [TestMethod]
    public void ProtectedServerArgumentsCannotBeOverridden()
    {
        using var gamePath = new TemporaryGamePath();
        var runner = new RecordingRunner();

        new KoreanGameLauncher().LaunchGame(
            runner,
            "official-token",
            "DEV.TestSID=attacker DEV.LobbyHost01=attacker.example custom=value",
            gamePath.Directory,
            DpiAwareness.Aware);

        Assert.IsNotNull(runner.Arguments);
        Assert.IsFalse(runner.Arguments.Contains("attacker", StringComparison.Ordinal));
        StringAssert.Contains(runner.Arguments, " DEV.TestSID=official-token");
        StringAssert.Contains(runner.Arguments, " DEV.LobbyHost01=nlobbyf-live.ff14.co.kr");
        StringAssert.Contains(runner.Arguments, " custom=value");
        Assert.IsFalse(runner.Arguments.StartsWith("//**sqex", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WineUserPathWithSpacesIsPreservedAsOneArgument()
    {
        using var gamePath = new TemporaryGamePath();
        var runner = new RecordingRunner();
        const string userPath = @"Z:\Users\Test User\Documents\My Games\FINAL FANTASY XIV - KOREA";

        new KoreanGameLauncher().LaunchGame(
            runner,
            "official-token",
            $"UserPath={userPath}",
            gamePath.Directory,
            DpiAwareness.Unaware);

        Assert.IsNotNull(runner.ArgumentList);
        CollectionAssert.Contains(
            (System.Collections.ICollection)runner.ArgumentList,
            $"UserPath={userPath}");
    }

    [TestMethod]
    public void PrefixLocalWineUserPathIsPreservedAsOneArgument()
    {
        using var gamePath = new TemporaryGamePath();
        var runner = new RecordingRunner();
        const string userPath = @"C:\xomkr-config";

        new KoreanGameLauncher().LaunchGame(
            runner,
            "official-token",
            $"UserPath={userPath}",
            gamePath.Directory,
            DpiAwareness.Unaware);

        Assert.IsNotNull(runner.ArgumentList);
        CollectionAssert.Contains(
            (System.Collections.ICollection)runner.ArgumentList,
            $"UserPath={userPath}");
    }

    private sealed class RecordingRunner : IArgumentListGameRunner
    {
        public string? Arguments { get; private set; }
        public IReadOnlyList<string>? ArgumentList { get; private set; }

        public Process? Start(
            string path,
            string workingDirectory,
            string arguments,
            IDictionary<string, string> environment,
            DpiAwareness dpiAwareness)
        {
            Arguments = arguments;
            return null;
        }

        public Process? Start(
            string path,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IDictionary<string, string> environment,
            DpiAwareness dpiAwareness)
        {
            ArgumentList = arguments;
            Arguments = string.Concat(arguments.Select(argument => $" {argument}"));
            return null;
        }
    }

    private sealed class TemporaryGamePath : IDisposable
    {
        public TemporaryGamePath()
        {
            Directory = new DirectoryInfo(Path.Combine(
                Path.GetTempPath(), $"xom-korea-launch-{Guid.NewGuid():N}"));
            var gameDirectory = System.IO.Directory.CreateDirectory(
                Path.Combine(Directory.FullName, "game"));
            using var executable = File.Create(Path.Combine(
                gameDirectory.FullName, "ffxiv_dx11.exe"));
        }

        public DirectoryInfo Directory { get; }

        public void Dispose()
        {
            if (Directory.Exists)
                Directory.Delete(true);
        }
    }
}
