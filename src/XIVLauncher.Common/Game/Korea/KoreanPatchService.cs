using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Patch.PatchList;

namespace XIVLauncher.Common.Game.Korea;

public interface IKoreanPatchService
{
    Task<KoreanPatchPlan> CheckAsync(DirectoryInfo gamePath, CancellationToken cancellationToken);
}

public sealed class KoreanPatchService : IKoreanPatchService
{
    private const long MaxPatchListBytes = 8 * 1024 * 1024;

    private static readonly Uri VersionEndpoint =
        new("http://ngamever-live.ff14.co.kr/http/win32/actoz_release_ko_game/");

    private static readonly Repository[] GameRepositories =
    {
        Repository.Ffxiv,
        Repository.Ex1,
        Repository.Ex2,
        Repository.Ex3,
        Repository.Ex4,
        Repository.Ex5,
    };

    private static readonly Regex VersionPattern =
        new(@"^(?:H)?\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}[a-z]{0,2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient client;

    public KoreanPatchService(HttpClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<KoreanPatchPlan> CheckAsync(
        DirectoryInfo gamePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gamePath);

        var versions = GameRepositories.ToDictionary(
            repository => repository,
            repository => repository.GetVer(gamePath).Trim());
        var ffxivVersionFile = Repository.Ffxiv.GetVerFile(gamePath);
        var isFreshInstall = !ffxivVersionFile.Exists
                             || string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(
                                 ffxivVersionFile.FullName, cancellationToken).ConfigureAwait(false));

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                new Uri(VersionEndpoint, Uri.EscapeDataString(versions[Repository.Ffxiv])),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new KoreanPatchException(KoreanPatchError.NetworkFailure, "VersionCheck", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new KoreanPatchException(KoreanPatchError.NetworkFailure, "VersionCheck", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new KoreanPatchException(KoreanPatchError.NetworkFailure, "VersionCheck");

            var contentType = response.Content.Headers.ContentType;
            if (!string.Equals(contentType?.MediaType, "multipart/mixed", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaxPatchListBytes
                || !response.Headers.TryGetValues("X-Repository", out var repositoryHeaders)
                || !repositoryHeaders.Contains("actoz/win32/release_ko/game", StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-Patch-Module", out var moduleHeaders)
                || !moduleHeaders.Contains("ZiPatch", StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-Latest-Version", out var latestVersionHeaders)
                || latestVersionHeaders.SingleOrDefault() is not { } latestVersion
                || !VersionPattern.IsMatch(latestVersion))
            {
                throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "VersionCheck");
            }

            var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

            var boundary = contentType!.Parameters
                .SingleOrDefault(parameter =>
                    string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim('"');
            if (string.IsNullOrWhiteSpace(boundary)
                || !body.TrimEnd().EndsWith($"--{boundary}--", StringComparison.Ordinal))
            {
                throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
            }

            var allPatches = ParsePatchList(body);
            if (isFreshInstall
                && GameRepositories.Any(repository => allPatches.All(patch => patch.GetRepo() != repository)))
            {
                throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
            }

            if (allPatches.Length == 0
                && (isFreshInstall
                    || !string.Equals(latestVersion, versions[Repository.Ffxiv], StringComparison.Ordinal)))
            {
                throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
            }

            var pending = new HashSet<PatchListEntry>();
            foreach (var repositoryGroup in allPatches.GroupBy(patch => patch.GetRepo()))
            {
                var patches = repositoryGroup.ToArray();
                var installedVersion = versions[repositoryGroup.Key];

                if (repositoryGroup.Key == Repository.Ffxiv
                    || isFreshInstall
                    || installedVersion == Constants.BASE_GAME_VERSION)
                {
                    pending.UnionWith(patches);
                    continue;
                }

                var installedIndex = Array.FindLastIndex(
                    patches,
                    patch => string.Equals(patch.VersionId, installedVersion, StringComparison.Ordinal));
                if (installedIndex < 0)
                {
                    throw new KoreanPatchException(
                        KoreanPatchError.InstalledVersionNotInPatchChain,
                        "VersionCheck");
                }

                for (var i = installedIndex + 1; i < patches.Length; i++)
                    pending.Add(patches[i]);
            }

            return new KoreanPatchPlan(
                new KoreanVersionReport(versions),
                allPatches.Where(pending.Contains).ToArray(),
                isFreshInstall);
        }
    }

    internal static PatchListEntry[] ParsePatchList(string body)
    {
        try
        {
            var patches = new List<PatchListEntry>();
            foreach (var rawLine in body.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                if (!rawLine.Contains('\t'))
                    continue;

                var fields = rawLine.Split('\t');
                if (fields.Length != 6
                    || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || length <= 0
                    || !VersionPattern.IsMatch(fields[4])
                    || !Uri.TryCreate(fields[5], UriKind.Absolute, out var patchUri)
                    || patchUri.Scheme is not ("http" or "https")
                    || !string.Equals(patchUri.Host, "client-patch-live.ff14.co.kr", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(patchUri.UserInfo)
                    || !string.IsNullOrEmpty(patchUri.Query)
                    || !string.IsNullOrEmpty(patchUri.Fragment)
                    || !IsSafePatchPath(patchUri.AbsolutePath))
                {
                    throw new FormatException("Malformed Korean patch row.");
                }

                patches.Add(new PatchListEntry
                {
                    Length = length,
                    VersionId = fields[4],
                    HashType = "zipatch",
                    HashBlockSize = 0,
                    Hashes = Array.Empty<string>(),
                    Url = new UriBuilder(patchUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri,
                });
            }

            return patches.ToArray();
        }
        catch (Exception exception) when (exception is not KoreanPatchException)
        {
            throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList", exception);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaxPatchListBytes)
                throw new KoreanPatchException(KoreanPatchError.ProtocolChanged, "PatchList");
            destination.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(destination.ToArray());
    }

    private static bool IsSafePatchPath(string path)
    {
        if (!path.StartsWith("/game/", StringComparison.Ordinal)
            || !path.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)
            || path.Contains('%')
            || path.Contains('\\'))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3
               && segments[0] == "game"
               && segments.All(segment =>
                   segment is not ("." or "..")
                   && segment.All(character =>
                       char.IsAsciiLetterOrDigit(character)
                       || character is '-' or '_' or '.'));
    }
}

public sealed class KoreanPatchPlan
{
    public KoreanPatchPlan(
        KoreanVersionReport installedVersions,
        IReadOnlyList<PatchListEntry> pendingPatches,
        bool isFreshInstall)
    {
        InstalledVersions = installedVersions;
        PendingPatches = pendingPatches;
        IsFreshInstall = isFreshInstall;
    }

    public KoreanVersionReport InstalledVersions { get; }

    public IReadOnlyList<PatchListEntry> PendingPatches { get; }

    public bool IsFreshInstall { get; }
}

public sealed class KoreanVersionReport
{
    private readonly IReadOnlyDictionary<Repository, string> versions;

    public KoreanVersionReport(IReadOnlyDictionary<Repository, string> versions)
    {
        this.versions = new Dictionary<Repository, string>(versions);
    }

    public string this[Repository repository] => versions[repository];
}

public enum KoreanPatchError
{
    NetworkFailure,
    ProtocolChanged,
    InstalledVersionNotInPatchChain,
}

public sealed class KoreanPatchException : Exception
{
    public KoreanPatchException(KoreanPatchError error, string stage, Exception? innerException = null)
        : base($"Korean patch operation failed during {stage}.", innerException)
    {
        Error = error;
        Stage = stage;
    }

    public KoreanPatchError Error { get; }

    public string Stage { get; }
}
