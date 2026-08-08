using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

#nullable enable

namespace XIVLauncher.Common.Dalamud
{
    public class DalamudUpdater
    {
        private readonly DirectoryInfo addonDirectory;
        private readonly DirectoryInfo assetRootDirectory;
        private readonly IUniqueIdCache? cache;
        private readonly DalamudReleaseSource releaseSource;

        private readonly TimeSpan defaultTimeout = TimeSpan.FromMinutes(15);

        private bool forceProxy = false;
        private DalamudVersionInfo? resolvedBranch;

        public DownloadState State { get; private set; } = DownloadState.Unknown;
        public bool IsStaging { get; private set; } = false;

        public Exception? EnsurementException { get; private set; }

        public string? CompatibilityWarning { get; private set; }

        private FileInfo? runnerInternal;

        public FileInfo Runner
        {
            get
            {
                if (RunnerOverride != null)
                    return RunnerOverride;

                return runnerInternal ?? throw new InvalidOperationException("Runner not prepared yet");
            }
            private set => runnerInternal = value;
        }

        public DirectoryInfo Runtime { get; }

        public FileInfo? RunnerOverride { get; set; }

        public DirectoryInfo? AssetDirectory { get; private set; }

        public IDalamudLoadingOverlay? Overlay { get; set; }

        public string? RolloutBucket { get; }

        public event Action<DalamudVersionInfo?>? ResolvedBranchChanged;

        public DalamudVersionInfo? ResolvedBranch
        {
            get => resolvedBranch;
            private set
            {
                if (resolvedBranch == value)
                    return;

                resolvedBranch = value;

                try
                {
                    ResolvedBranchChanged?.Invoke(resolvedBranch);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public enum DownloadState
        {
            Unknown,
            Running,
            Done,
            NoIntegrity, // fail with error message
        }

        public DalamudUpdater(
            DirectoryInfo addonDirectory,
            DirectoryInfo runtimeDirectory,
            DirectoryInfo assetRootDirectory,
            IUniqueIdCache? cache,
            string? dalamudRolloutBucket,
            DalamudReleaseSource? releaseSource = null)
        {
            this.addonDirectory = addonDirectory;
            this.assetRootDirectory = assetRootDirectory;

            this.Runtime = runtimeDirectory;
            this.AssetDirectory = null;
            this.cache = cache;
            this.releaseSource = releaseSource ?? DalamudReleaseSource.Global;

            this.RolloutBucket = dalamudRolloutBucket;

            if (this.RolloutBucket == null)
            {
                var rng = new Random();
                this.RolloutBucket = rng.Next(0, 9) >= 7 ? "Canary" : "Control";
            }
        }

        public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress)
        {
            Overlay!.SetStep(progress);
        }

        public void ShowOverlay()
        {
            Overlay!.SetVisible();
        }

        public void CloseOverlay()
        {
            Overlay!.SetInvisible();
        }

        private void ReportOverlayProgress(long? size, long downloaded, double? progress)
        {
            Overlay!.ReportProgress(size, downloaded, progress);
        }

        public void Run(string? betaKind, string? betaKey, bool overrideForceProxy = false)
        {
            if (this.State == DownloadState.Running)
            {
                Log.Information("[DUPDATE] An update is already running; ignoring the duplicate request.");
                return;
            }

            Log.Information("[DUPDATE] Starting... (forceProxy: {ForceProxy})", overrideForceProxy);
            this.State = DownloadState.Running;

            this.forceProxy = overrideForceProxy;

            this.ResolvedBranch = null;
            this.EnsurementException = null;
            this.CompatibilityWarning = null;
            this.IsStaging = false;

            Task.Run(async () =>
            {
                var maxTries = this.releaseSource == DalamudReleaseSource.Korean ? 3 : 10;

                var isUpdated = false;

                for (var tries = 0; tries < maxTries; tries++)
                {
                    try
                    {
                        await UpdateDalamud(betaKind, betaKey).ConfigureAwait(true);
                        isUpdated = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Update failed, try {TryCnt}/{MaxTries}...", tries + 1, maxTries);
                        this.EnsurementException = ex;
                        this.forceProxy = true;
                    }
                }

                this.State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            });
        }

        public bool? ReCheckVersion(DirectoryInfo gamePath)
        {
            if (this.State != DownloadState.Done)
                return null;

            if (this.RunnerOverride != null)
                return true;

            var info = DalamudVersionInfo.Load(new FileInfo(Path.Combine(this.Runner.DirectoryName!,
                "version.json")));

            var gameVersion = Repository.Ffxiv.GetVer(gamePath);
            if (gameVersion == info.SupportedGameVer)
                return true;

            this.CompatibilityWarning =
                $"The installed game version ({gameVersion}) differs from the Korean Dalamud manifest ({info.SupportedGameVer}).";

            if (!this.releaseSource.EnforceSupportedGameVersion)
            {
                Log.Warning("[DUPDATE] {CompatibilityWarning} Continuing because the Korean updater treats this as advisory.", this.CompatibilityWarning);
                return true;
            }

            return false;
        }

        private static string GetBetaTrackName(string betaKind) =>
            string.IsNullOrEmpty(betaKind) ? "staging" : betaKind;

        private async Task<(DalamudVersionInfo release, DalamudVersionInfo? staging)> GetVersionInfo(string? betaKind, string? betaKey)
        {
            using var client = new HttpClient
            {
                Timeout = this.defaultTimeout,
            };

            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = this.releaseSource == DalamudReleaseSource.Korean,
            };

            if (this.releaseSource.VersionInfoUrl != null)
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("XIV-on-Mac-KR/1.0");
                var koreanJson = await client.GetStringAsync(this.releaseSource.VersionInfoUrl).ConfigureAwait(false);
                var koreanInfo = JsonSerializer.Deserialize(koreanJson, DalamudJsonContext.Default.DalamudVersionInfo)
                    ?? throw new DalamudIntegrityException("The Korean Dalamud manifest was empty.");

                ValidateVersionInfo(koreanInfo);
                this.releaseSource.ValidateDalamudDownloadUrl(koreanInfo.DownloadUrl);
                return (koreanInfo, null);
            }

            var versionInfoJsonRelease = await client.GetStringAsync(DalamudLauncher.REMOTE_BASE + $"release&bucket={this.RolloutBucket}").ConfigureAwait(false);

            DalamudVersionInfo versionInfoRelease = JsonSerializer.Deserialize(versionInfoJsonRelease, DalamudJsonContext.Default.DalamudVersionInfo);

            DalamudVersionInfo? versionInfoStaging = null;

            if (!string.IsNullOrEmpty(betaKey))
            {
                var versionInfoJsonStaging = await client.GetAsync(DalamudLauncher.REMOTE_BASE + GetBetaTrackName(betaKind)).ConfigureAwait(false);

                if (versionInfoJsonStaging.StatusCode != HttpStatusCode.BadRequest)
                    versionInfoStaging = JsonSerializer.Deserialize(await versionInfoJsonStaging.Content.ReadAsStringAsync().ConfigureAwait(false), DalamudJsonContext.Default.DalamudVersionInfo);
            }

            return (versionInfoRelease, versionInfoStaging);
        }

        private static void ValidateVersionInfo(DalamudVersionInfo versionInfo)
        {
            if (string.IsNullOrWhiteSpace(versionInfo.AssemblyVersion)
                || string.IsNullOrWhiteSpace(versionInfo.SupportedGameVer)
                || string.IsNullOrWhiteSpace(versionInfo.RuntimeVersion)
                || string.IsNullOrWhiteSpace(versionInfo.DownloadUrl))
            {
                throw new DalamudIntegrityException("The Dalamud manifest is missing required fields.");
            }
        }

        private async Task UpdateDalamud(string? betaKind, string? betaKey)
        {
            // GitHub requires TLS 1.2, we need to hardcode this for Windows 7
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var (versionInfoRelease, versionInfoStaging) = await GetVersionInfo(betaKind, betaKey).ConfigureAwait(false);

            var remoteVersionInfo = versionInfoRelease;

            if (versionInfoStaging?.Key != null && versionInfoStaging.Key == betaKey)
            {
                remoteVersionInfo = versionInfoStaging;
                IsStaging = true;
                Log.Information("[DUPDATE] Using staging version {Kind} with key {Key} ({Hash})", betaKind, betaKey, remoteVersionInfo.AssemblyVersion);
            }
            else
            {
                Log.Information("[DUPDATE] Using release version ({Hash})", remoteVersionInfo.AssemblyVersion);
            }

            // Update resolved branch to reflect what the server actually selected
            this.ResolvedBranch = remoteVersionInfo;

            var versionInfoJson = JsonSerializer.Serialize(remoteVersionInfo, DalamudJsonContext.Default.DalamudVersionInfo);

            var addonPath = new DirectoryInfo(Path.Combine(this.addonDirectory.FullName, "Hooks"));
            var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName, remoteVersionInfo.AssemblyVersion));
            var runtimePaths = new DirectoryInfo[]
            {
                new(Path.Combine(this.Runtime.FullName, "host", "fxr", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.NETCore.App", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", remoteVersionInfo.RuntimeVersion)),
            };

            if (!currentVersionPath.Exists || !IsIntegrity(currentVersionPath, remoteVersionInfo.Hash))
            {
                Log.Information("[DUPDATE] Not found, redownloading");

                SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

                try
                {
                    await DownloadDalamud(currentVersionPath, remoteVersionInfo).ConfigureAwait(true);
                    CleanUpOld(addonPath, remoteVersionInfo.AssemblyVersion, this.releaseSource == DalamudReleaseSource.Korean);

                    // This is a good indicator that we should clear the UID cache
                    cache?.Reset();
                }
                catch (Exception ex)
                {
                    throw new DalamudIntegrityException("Could not download Dalamud", ex);
                }
            }

            if (remoteVersionInfo.RuntimeRequired)
            {
                Log.Information("[DUPDATE] Now starting for .NET Runtime {0}", remoteVersionInfo.RuntimeVersion);

                var versionFile = new FileInfo(Path.Combine(this.Runtime.FullName, "version"));
                var localVersion = GetLocalRuntimeVersion(versionFile);

                var runtimeNeedsUpdate = localVersion != remoteVersionInfo.RuntimeVersion;

                if (!this.Runtime.Exists)
                    Directory.CreateDirectory(this.Runtime.FullName);

                var isRuntimeIntegrity = false;

                // Only check runtime hashes if we don't need to update it
                if (!runtimeNeedsUpdate)
                {
                    try
                    {
                        isRuntimeIntegrity = this.releaseSource.UseOfficialMicrosoftRuntime
                            ? CheckKoreanRuntimeIntegrity(Runtime, localVersion)
                            : await CheckRuntimeHashes(Runtime, localVersion).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Could not check runtime integrity.");
                    }
                }

                if (runtimePaths.Any(p => !p.Exists) || runtimeNeedsUpdate || !isRuntimeIntegrity)
                {
                    Log.Information("[DUPDATE] Not found, outdated or no integrity: {LocalVer} - {RemoteVer}", localVersion, remoteVersionInfo.RuntimeVersion);

                    SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

                    try
                    {
                        Log.Verbose("[DUPDATE] Now download runtime...");
                        if (this.releaseSource.UseOfficialMicrosoftRuntime)
                            await DownloadKoreanRuntime(this.Runtime, remoteVersionInfo.RuntimeVersion).ConfigureAwait(false);
                        else
                        {
                            await DownloadRuntime(this.Runtime, remoteVersionInfo.RuntimeVersion).ConfigureAwait(false);
                            File.WriteAllText(versionFile.FullName, remoteVersionInfo.RuntimeVersion);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new DalamudIntegrityException("Could not ensure runtime", ex);
                    }
                }
            }

            Log.Verbose("[DUPDATE] Now ensure assets...");

            var assetVer = 0;

            try
            {
                this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
                this.ReportOverlayProgress(null, 0, null);
                var assetResult = this.releaseSource.UseKoreanAssets
                    ? await AssetManager.EnsureKoreanAssets(this, this.assetRootDirectory).ConfigureAwait(true)
                    : await AssetManager.EnsureAssets(this, this.assetRootDirectory).ConfigureAwait(true);
                AssetDirectory = assetResult.AssetDir;
                assetVer = assetResult.Version;
            }
            catch (Exception ex)
            {
                throw new DalamudIntegrityException("Could not ensure assets", ex);
            }

            if (!IsIntegrity(currentVersionPath, remoteVersionInfo.Hash))
            {
                throw new DalamudIntegrityException("No integrity after ensurement");
            }

            WriteVersionJson(currentVersionPath, versionInfoJson);

            Log.Information("[DUPDATE] All set for {GameVersion} with {DalamudVersion}({RuntimeVersion}, {AssetVersion})", remoteVersionInfo.SupportedGameVer, remoteVersionInfo.AssemblyVersion, remoteVersionInfo.RuntimeVersion, assetVer);

            Runner = new FileInfo(Path.Combine(currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
            ReportOverlayProgress(null, 0, null);
        }

        private static bool CanRead(FileInfo info)
        {
            try
            {
                using var stream = info.OpenRead();
                stream.ReadByte();
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool IsIntegrity(DirectoryInfo addonPath, string? expectedHashesHash = null)
        {
            var files = addonPath.GetFiles();

            try
            {
                if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe"))
                    || !CanRead(files.First(x => x.Name == "Dalamud.dll"))
                    || !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
                {
                    Log.Error("[DUPDATE] Can't open files for read");
                    return false;
                }

                var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

                if (!File.Exists(hashesPath))
                {
                    Log.Error("[DUPDATE] No hashes.json");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(expectedHashesHash))
                {
                    using var hashesStream = File.OpenRead(hashesPath);
                    using var hashesMd5 = MD5.Create();
                    var actualHashesHash = Convert.ToHexString(hashesMd5.ComputeHash(hashesStream));
                    if (!string.Equals(actualHashesHash, expectedHashesHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error(
                            "[DUPDATE] hashes.json integrity check failed ({Expected} - {Actual})",
                            expectedHashesHash,
                            actualHashesHash);
                        return false;
                    }
                }

                return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] No dalamud integrity");
                return false;
            }
        }

        internal static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
        {
            try
            {
                Log.Verbose("[DUPDATE] Checking integrity of {Directory}", directory.FullName);

                var hashes = JsonSerializer.Deserialize(hashesJson, DalamudJsonContext.Default.DictionaryStringString);

                if (hashes == null || hashes.Count == 0)
                    return false;

                foreach (var hash in hashes)
                {
                    var file = ResolvePathUnderRoot(directory, hash.Key);
                    using var fileStream = File.OpenRead(file);
                    using var md5 = MD5.Create();

                    var hashed = BitConverter.ToString(md5.ComputeHash(fileStream)).ToUpperInvariant().Replace("-", string.Empty);

                    if (!string.Equals(hashed, hash.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error("[DUPDATE] Integrity check failed for {0} ({1} - {2})", file, hash.Value, hashed);
                        return false;
                    }

                    Log.Verbose("[DUPDATE] Integrity check OK for {0} ({1})", file, hashed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Integrity check failed");
                return false;
            }

            return true;
        }

        internal static string ResolvePathUnderRoot(DirectoryInfo root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new DalamudIntegrityException("A Dalamud package contained an invalid path.");

            var normalizedRelativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar)
                                                     .Replace('/', Path.DirectorySeparatorChar);
            var rootPath = Path.GetFullPath(root.FullName)
                               .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            var candidatePath = Path.GetFullPath(Path.Combine(rootPath, normalizedRelativePath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!candidatePath.StartsWith(rootPath, comparison))
                throw new DalamudIntegrityException("A Dalamud package attempted to write outside its cache.");

            return candidatePath;
        }

        private static void CleanUpOld(DirectoryInfo addonPath, string currentVer, bool keepPrevious)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!addonPath.Exists)
                return;

            var oldDirectories = addonPath.GetDirectories()
                .Where(directory => directory.Name != "dev" && directory.Name != currentVer)
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .ToArray();

            foreach (var directory in keepPrevious ? oldDirectories.Skip(1) : oldDirectories)
            {
                try
                {
                    directory.Delete(true);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static void WriteVersionJson(DirectoryInfo addonPath, string info)
        {
            File.WriteAllText(Path.Combine(addonPath.FullName, "version.json"), info);
        }

        private async Task DownloadDalamud(DirectoryInfo addonPath, DalamudVersionInfo version)
        {
            var downloadPath = PlatformHelpers.GetTempFileName();
            var stagingPath = new DirectoryInfo(
                Path.Combine(addonPath.Parent!.FullName, $".{addonPath.Name}.staging-{Guid.NewGuid():N}"));
            DirectoryInfo? backupPath = null;

            try
            {
                stagingPath.Create();
                await this.DownloadFile(version.DownloadUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
                ExtractZipSafely(downloadPath, stagingPath);

                if (!IsIntegrity(stagingPath, version.Hash))
                    throw new DalamudIntegrityException("The downloaded Korean Dalamud package failed integrity verification.");

                if (addonPath.Exists)
                {
                    backupPath = new DirectoryInfo(
                        Path.Combine(addonPath.Parent.FullName, $".{addonPath.Name}.backup-{Guid.NewGuid():N}"));
                    Directory.Move(addonPath.FullName, backupPath.FullName);
                }

                Directory.Move(stagingPath.FullName, addonPath.FullName);
                try
                {
                    backupPath?.Delete(true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DUPDATE] Could not remove the replaced Dalamud cache.");
                }

                try
                {
                    var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));
                    PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                    PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DUPDATE] Could not refresh the Dalamud development cache.");
                }
            }
            catch
            {
                if (!addonPath.Exists && backupPath?.Exists == true)
                    Directory.Move(backupPath.FullName, addonPath.FullName);
                throw;
            }
            finally
            {
                if (File.Exists(downloadPath))
                    File.Delete(downloadPath);
                if (stagingPath.Exists)
                    stagingPath.Delete(true);
            }
        }

        private static void ExtractZipSafely(string archivePath, DirectoryInfo destination)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName))
                    continue;

                var targetPath = ResolvePathUnderRoot(destination, entry.FullName);
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
                    || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, true);
            }
        }

        private string GetLocalRuntimeVersion(FileInfo versionFile)
        {
            // This is the version we first shipped. We didn't write out a version file, so we can't check it.
            var localVersion = "5.0.6";

            try
            {
                if (versionFile.Exists)
                    localVersion = File.ReadAllText(versionFile.FullName).Trim();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not read local runtime version.");
            }

            return localVersion;
        }

        private async Task<bool> CheckRuntimeHashes(DirectoryInfo runtimePath, string version)
        {
            var hashesFile = new FileInfo(Path.Combine(runtimePath.FullName, $"hashes-{version}.json"));
            string? runtimeHashes = null;

            if (!hashesFile.Exists)
            {
                Log.Verbose("[DUPDATE] Hashes file does not exist, redownloading...");

                try
                {
                    using var client = new HttpClient();
                    runtimeHashes = await client.GetStringAsync($"https://kamori.goats.dev/Dalamud/Release/Runtime/Hashes/{version}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DUPDATE] Could not download hashes for runtime v{Version}", version);
                    return false;
                }

                File.WriteAllText(hashesFile.FullName, runtimeHashes);
            }
            else
            {
                runtimeHashes = File.ReadAllText(hashesFile.FullName);
            }

            return CheckIntegrity(runtimePath, runtimeHashes);
        }

        private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
        {
            // Ensure directory exists
            if (!runtimePath.Exists)
            {
                runtimePath.Create();
            }
            else
            {
                runtimePath.Delete(true);
                runtimePath.Create();
            }

            // Wait for it to be gone, thanks Windows
            Thread.Sleep(1000);

            var dotnetUrl = $"https://kamori.goats.dev/Dalamud/Release/Runtime/DotNet/{version}";
            var desktopUrl = $"https://kamori.goats.dev/Dalamud/Release/Runtime/WindowsDesktop/{version}";

            var downloadPath = PlatformHelpers.GetTempFileName();

            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            await this.DownloadFile(dotnetUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

            await this.DownloadFile(desktopUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

            File.Delete(downloadPath);
        }

        private async Task DownloadKoreanRuntime(DirectoryInfo runtimePath, string version)
        {
            version = ValidateRuntimeVersion(version);

            var runtimeUrl = $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/dotnet-runtime-{version}-win-x64.zip";
            var desktopUrl = $"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-x64.zip";
            var downloadPath = PlatformHelpers.GetTempFileName();
            var parentPath = runtimePath.Parent
                ?? throw new DalamudIntegrityException("The runtime cache has no parent directory.");
            var stagingPath = new DirectoryInfo(
                Path.Combine(parentPath.FullName, $".{runtimePath.Name}.staging-{Guid.NewGuid():N}"));
            DirectoryInfo? backupPath = null;

            try
            {
                stagingPath.Create();

                await this.DownloadFileWithSha512(runtimeUrl, downloadPath).ConfigureAwait(false);
                ExtractZipSafely(downloadPath, stagingPath);

                await this.DownloadFileWithSha512(desktopUrl, downloadPath).ConfigureAwait(false);
                ExtractZipSafely(downloadPath, stagingPath);

                var requiredPaths = new[]
                {
                    Path.Combine(stagingPath.FullName, "host", "fxr", version),
                    Path.Combine(stagingPath.FullName, "shared", "Microsoft.NETCore.App", version),
                    Path.Combine(stagingPath.FullName, "shared", "Microsoft.WindowsDesktop.App", version),
                };
                if (requiredPaths.Any(path => !Directory.Exists(path)))
                    throw new DalamudIntegrityException("The downloaded Microsoft runtime package was incomplete.");

                File.WriteAllText(Path.Combine(stagingPath.FullName, "version"), version);

                if (runtimePath.Exists)
                {
                    backupPath = new DirectoryInfo(
                        Path.Combine(parentPath.FullName, $".{runtimePath.Name}.backup-{Guid.NewGuid():N}"));
                    Directory.Move(runtimePath.FullName, backupPath.FullName);
                }

                Directory.Move(stagingPath.FullName, runtimePath.FullName);
                if (backupPath?.Exists == true)
                    backupPath.Delete(true);
            }
            catch
            {
                if (!runtimePath.Exists && backupPath?.Exists == true)
                    Directory.Move(backupPath.FullName, runtimePath.FullName);
                throw;
            }
            finally
            {
                if (File.Exists(downloadPath))
                    File.Delete(downloadPath);
                if (stagingPath.Exists)
                    stagingPath.Delete(true);
            }
        }

        internal static string ValidateRuntimeVersion(string version)
        {
            var segments = version.Split('.');
            if (segments.Length is < 2 or > 4
                || segments.Any(segment => segment.Length == 0 || segment.Any(character => !char.IsDigit(character)))
                || !Version.TryParse(version, out var parsedVersion)
                || parsedVersion.Major <= 0)
            {
                throw new DalamudIntegrityException("The Korean Dalamud manifest contained an invalid runtime version.");
            }

            return version;
        }

        internal static bool CheckKoreanRuntimeIntegrity(DirectoryInfo runtimePath, string version)
        {
            try
            {
                version = ValidateRuntimeVersion(version);
                var versionFile = new FileInfo(Path.Combine(runtimePath.FullName, "version"));
                if (!versionFile.Exists
                    || !string.Equals(File.ReadAllText(versionFile.FullName).Trim(), version, StringComparison.Ordinal))
                {
                    return false;
                }

                var requiredFiles = new[]
                {
                    new FileInfo(Path.Combine(runtimePath.FullName, "dotnet.exe")),
                    new FileInfo(Path.Combine(runtimePath.FullName, "host", "fxr", version, "hostfxr.dll")),
                    new FileInfo(Path.Combine(runtimePath.FullName, "shared", "Microsoft.NETCore.App", version, "coreclr.dll")),
                    new FileInfo(Path.Combine(runtimePath.FullName, "shared", "Microsoft.WindowsDesktop.App", version, "PresentationFramework.dll")),
                };

                return requiredFiles.All(file => file.Exists && file.Length > 0);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DUPDATE] The Korean Microsoft runtime cache failed its layout check.");
                return false;
            }
        }

        private async Task DownloadFileWithSha512(string url, string downloadPath)
        {
            var hashPath = PlatformHelpers.GetTempFileName();
            try
            {
                await this.DownloadFile(url + ".sha512", hashPath, this.defaultTimeout).ConfigureAwait(false);
                var expectedHash = File.ReadAllText(hashPath)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (expectedHash == null
                    || expectedHash.Length != 128
                    || expectedHash.Any(character => !char.IsAsciiHexDigit(character)))
                {
                    throw new DalamudIntegrityException("The Microsoft runtime checksum response was invalid.");
                }

                await this.DownloadFile(url, downloadPath, this.defaultTimeout).ConfigureAwait(false);
                using var file = File.OpenRead(downloadPath);
                using var sha512 = SHA512.Create();
                var actualHash = Convert.ToHexString(sha512.ComputeHash(file));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new DalamudIntegrityException("The Microsoft runtime package failed SHA-512 verification.");
            }
            finally
            {
                if (File.Exists(hashPath))
                    File.Delete(hashPath);
            }
        }

        public async Task DownloadFile(string url, string path, TimeSpan timeout)
        {
            if (this.forceProxy && url.Contains("/File/Get/"))
            {
                url = url.Replace("/File/Get/", "/File/GetProxy/");
            }

            using var downloader = new HttpClientDownloadWithProgress(url, path);
            downloader.ProgressChanged += this.ReportOverlayProgress;

            await downloader.Download(timeout).ConfigureAwait(false);
        }
    }

    public class DalamudIntegrityException : Exception
    {
        public DalamudIntegrityException(string msg, Exception? inner = null)
            : base(msg, inner)
        {
        }
    }
}

#nullable restore
