using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.Korea;

namespace XIVLauncher.Common.Tests.Game.Korea;

[TestClass]
public sealed class KoreanPatchServiceTests
{
    private const string Boundary = "fixture-boundary";

    [TestMethod]
    public async Task FreshInstallUsesCompleteHttpsAllowlistedPatchChain()
    {
        using var gamePath = new TemporaryGamePath();
        var body = PatchList(
            Row("game/basehash/H2024.11.02.0000.0000aa.patch", "H2024.11.02.0000.0000aa"),
            Row("game/ex1/hash/D2026.06.18.0000.0000.patch", "2026.06.18.0000.0000"),
            Row("game/ex2/hash/D2026.06.18.0000.0000.patch", "2026.06.18.0000.0000"),
            Row("game/ex3/hash/D2026.06.18.0000.0000.patch", "2026.06.18.0000.0000"),
            Row("game/ex4/hash/D2026.06.18.0000.0000.patch", "2026.06.18.0000.0000"),
            Row("game/ex5/hash/D2026.06.18.0000.0000.patch", "2026.06.18.0000.0000"));
        using var client = new HttpClient(new VersionResponseHandler(body));

        var plan = await new KoreanPatchService(client).CheckAsync(
            gamePath.Directory, CancellationToken.None);

        Assert.IsTrue(plan.IsFreshInstall);
        Assert.AreEqual(6, plan.PendingPatches.Count);
        Assert.IsTrue(plan.PendingPatches.All(patch =>
            patch.Url.StartsWith("https://client-patch-live.ff14.co.kr/game/", StringComparison.Ordinal)));
        Assert.IsTrue(plan.PendingPatches.All(patch => patch.HashType == "zipatch"));
    }

    [TestMethod]
    public async Task UnexpectedPatchHostFailsClosed()
    {
        using var gamePath = new TemporaryGamePath();
        using var client = new HttpClient(new VersionResponseHandler(PatchList(
            "100\t100\t1\t1\t2026.06.18.0000.0000\thttps://example.invalid/game/file.patch")));

        var exception = await Assert.ThrowsExactlyAsync<KoreanPatchException>(() =>
            new KoreanPatchService(client).CheckAsync(gamePath.Directory, CancellationToken.None));

        Assert.AreEqual(KoreanPatchError.ProtocolChanged, exception.Error);
    }

    [TestMethod]
    public async Task EncodedPatchPathFailsClosed()
    {
        using var gamePath = new TemporaryGamePath();
        using var client = new HttpClient(new VersionResponseHandler(PatchList(
            "100\t100\t1\t1\t2026.06.18.0000.0000\thttps://client-patch-live.ff14.co.kr/game/%2e%2e/escape.patch")));

        var exception = await Assert.ThrowsExactlyAsync<KoreanPatchException>(() =>
            new KoreanPatchService(client).CheckAsync(gamePath.Directory, CancellationToken.None));

        Assert.AreEqual(KoreanPatchError.ProtocolChanged, exception.Error);
    }

    private static string PatchList(params string[] rows)
    {
        return $"--{Boundary}\r\nContent-Type: application/octet-stream\r\n\r\n"
               + string.Join("\r\n", rows)
               + $"\r\n--{Boundary}--\r\n";
    }

    private static string Row(string path, string version)
    {
        return $"100\t100\t1\t1\t{version}\thttp://client-patch-live.ff14.co.kr/{path}";
    }

    private sealed class VersionResponseHandler : HttpMessageHandler
    {
        private readonly string body;

        public VersionResponseHandler(string body)
        {
            this.body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.ASCII, "multipart/mixed"),
            };
            response.Content.Headers.ContentType!.Parameters.Add(new("boundary", Boundary));
            response.Headers.Add("X-Repository", "actoz/win32/release_ko/game");
            response.Headers.Add("X-Patch-Module", "ZiPatch");
            response.Headers.Add("X-Latest-Version", "2026.06.18.0000.0000");
            return Task.FromResult(response);
        }
    }

    private sealed class TemporaryGamePath : IDisposable
    {
        public TemporaryGamePath()
        {
            Directory = new DirectoryInfo(Path.Combine(
                Path.GetTempPath(), $"xom-korea-patch-{Guid.NewGuid():N}"));
            Directory.Create();
        }

        public DirectoryInfo Directory { get; }

        public void Dispose()
        {
            if (Directory.Exists)
                Directory.Delete(true);
        }
    }
}
