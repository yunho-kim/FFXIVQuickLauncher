using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Dalamud;

namespace XIVLauncher.Common.Tests;

[TestClass]
public sealed class DalamudKoreanTests
{
    [TestMethod]
    public void KoreanManifestUsesPascalCaseAndRetainsHash()
    {
        const string json = """
            {
              "AssemblyVersion":"15.0.3.0",
              "SupportedGameVer":"2026.07.16.0001.0000",
              "RuntimeVersion":"10.0.2",
              "RuntimeRequired":true,
              "Hash":"BFF1E445CEB24D9D69D78936FBD5BAC8",
              "downloadUrl":"https://raw.githubusercontent.com/dal4kr/Dalamud.Resources/refs/heads/main/Dalamud/latest/Release.zip",
              "track":"release"
            }
            """;

        var info = JsonSerializer.Deserialize(json, DalamudJsonContext.Default.DalamudVersionInfo);

        Assert.IsNotNull(info);
        Assert.AreEqual("15.0.3.0", info.AssemblyVersion);
        Assert.AreEqual("BFF1E445CEB24D9D69D78936FBD5BAC8", info.Hash);
        Assert.AreEqual("10.0.2", info.RuntimeVersion);
    }

    [TestMethod]
    public void KoreanInjectorLanguageUsesNameInsteadOfLauncherEnumValue()
    {
        Assert.AreEqual(
            "--dalamud-client-language=korean",
            DalamudInjectorArgs.ClientLanguage("korean"));
        Assert.AreNotEqual(
            DalamudInjectorArgs.ClientLanguage((int)ClientLanguage.Korean),
            DalamudInjectorArgs.ClientLanguage("korean"));
    }

    [TestMethod]
    public void IntegrityPathsCannotEscapeCacheRoot()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        root.Create();
        try
        {
            Assert.ThrowsException<DalamudIntegrityException>(
                () => DalamudUpdater.ResolvePathUnderRoot(root, "../outside.dll"));
            Assert.IsFalse(DalamudUpdater.CheckIntegrity(root, "{\"../outside.dll\":\"00\"}"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [TestMethod]
    public void KoreanReleaseSourceRejectsUnexpectedHost()
    {
        DalamudReleaseSource.Korean.ValidateDalamudDownloadUrl(
            "https://raw.githubusercontent.com/dal4kr/Dalamud.Resources/refs/heads/main/Dalamud/latest/Release.zip");

        Assert.ThrowsException<DalamudIntegrityException>(() =>
            DalamudReleaseSource.Korean.ValidateDalamudDownloadUrl("https://example.com/Release.zip"));
    }

    [TestMethod]
    public void KoreanRuntimeVersionRejectsPathLikeValues()
    {
        Assert.AreEqual("10.0.2", DalamudUpdater.ValidateRuntimeVersion("10.0.2"));
        Assert.ThrowsException<DalamudIntegrityException>(() =>
            DalamudUpdater.ValidateRuntimeVersion("10..2"));
        Assert.ThrowsException<DalamudIntegrityException>(() =>
            DalamudUpdater.ValidateRuntimeVersion("../10.0.2"));
    }

    [TestMethod]
    public void KoreanRuntimeIntegrityRequiresVersionAndCoreFiles()
    {
        const string version = "10.0.2";
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        root.Create();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "version"), version);
            WriteRequiredRuntimeFile(root, "dotnet.exe");
            WriteRequiredRuntimeFile(root, "host", "fxr", version, "hostfxr.dll");
            WriteRequiredRuntimeFile(root, "shared", "Microsoft.NETCore.App", version, "coreclr.dll");
            WriteRequiredRuntimeFile(root, "shared", "Microsoft.WindowsDesktop.App", version, "PresentationFramework.dll");

            Assert.IsTrue(DalamudUpdater.CheckKoreanRuntimeIntegrity(root, version));
            File.Delete(Path.Combine(root.FullName, "host", "fxr", version, "hostfxr.dll"));
            Assert.IsFalse(DalamudUpdater.CheckKoreanRuntimeIntegrity(root, version));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static void WriteRequiredRuntimeFile(DirectoryInfo root, params string[] pathParts)
    {
        var path = pathParts.Aggregate(root.FullName, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "runtime");
    }
}
