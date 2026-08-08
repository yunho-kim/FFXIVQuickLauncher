using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using Serilog;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud
{
    public class AssetManager
    {
        private const string ASSET_STORE_URL = "https://kamori.goats.dev/Dalamud/Asset/Meta?appId=xom";
        private const string KOREAN_ASSET_STORE_URL =
            "https://raw.githubusercontent.com/goatcorp/DalamudAssets/refs/heads/master/asset.json";

        internal class AssetInfo
        {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("assets")]
            public IReadOnlyList<Asset> Assets { get; set; }

            [JsonPropertyName("packageUrl")]
            public string PackageUrl { get; set; }

            public class Asset
            {
                [JsonPropertyName("url")]
                public string Url { get; set; }

                [JsonPropertyName("fileName")]
                public string FileName { get; set; }
                
                [JsonPropertyName("hash")]
                public string Hash { get; set; }
            }
        }

        public static async Task<(DirectoryInfo AssetDir, int Version)> EnsureAssets(DalamudUpdater updater, DirectoryInfo baseDir)
        {
            using var metaClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30),
            };

            metaClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
            };

            using var sha1 = SHA1.Create();

            Log.Verbose("[DASSET] Starting asset download");

            var (isRefreshNeeded, info) = await CheckAssetRefreshNeeded(metaClient, baseDir);

            // NOTE(goat): We should use a junction instead of copying assets to a new folder. There is no C# API for junctions in .NET Framework.

            var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, info.Version.ToString()));
            var devDir = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));

            // If we don't need a refresh, let's check if all hashes are good
            if (!isRefreshNeeded)
            {
                foreach (var entry in info.Assets)
                {
                    var filePath = Path.Combine(currentDir.FullName, entry.FileName);

                    if (!File.Exists(filePath))
                    {
                        Log.Error("[DASSET] {0} not found locally", entry.FileName);
                        isRefreshNeeded = true;
                        break;
                    }

                    if (string.IsNullOrEmpty(entry.Hash))
                        continue;

                    try
                    {
                        using var file = File.OpenRead(filePath);
                        var fileHash = sha1.ComputeHash(file);
                        var stringHash = BitConverter.ToString(fileHash).Replace("-", "");

                        if (stringHash != entry.Hash)
                        {
                            Log.Error("[DASSET] {0} has {1}, remote {2}, need refresh", entry.FileName, stringHash, entry.Hash);
                            isRefreshNeeded = true;
                            //break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DASSET] Could not read asset");
                        isRefreshNeeded = true;
                        break;
                    }
                }
            }

            if (isRefreshNeeded)
            {
                PlatformHelpers.DeleteAndRecreateDirectory(currentDir);

                // Wait for it to be gone
                Thread.Sleep(1000);

                var packageUrl = info.PackageUrl;

                var tempPath = PlatformHelpers.GetTempFileName();

                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                await updater.DownloadFile(packageUrl, tempPath, TimeSpan.FromMinutes(4));

                using (var packageStream = File.OpenRead(tempPath))
                using (var packageArc = new ZipArchive(packageStream, ZipArchiveMode.Read))
                {
                    packageArc.ExtractToDirectory(currentDir.FullName);
                }

                try
                {
                    PlatformHelpers.DeleteAndRecreateDirectory(devDir);
                    PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DASSET] Could not copy to dev dir");
                }

                File.Delete(tempPath);
            }

            if (isRefreshNeeded)
                SetLocalAssetVer(baseDir, info.Version);

            Log.Verbose("[DASSET] Assets OK at {0}", currentDir.FullName);

            try
            {
                CleanUpOld(baseDir, devDir, currentDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET] Could not clean up old assets");
            }

            return (currentDir, info.Version);
        }

        public static async Task<(DirectoryInfo AssetDir, int Version)> EnsureKoreanAssets(
            DalamudUpdater updater,
            DirectoryInfo baseDir)
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            };
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XIV-on-Mac-KR/1.0");

            var json = await client.GetStringAsync(KOREAN_ASSET_STORE_URL).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize(json, AssetInfoJsonContext.Default.AssetInfo)
                ?? throw new DalamudIntegrityException("The Dalamud asset manifest was empty.");
            if (info.Version <= 0 || info.Assets == null || info.Assets.Count == 0)
                throw new DalamudIntegrityException("The Dalamud asset manifest was invalid.");

            var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, info.Version.ToString()));
            currentDir.Create();

            foreach (var entry in info.Assets)
            {
                var targetPath = DalamudUpdater.ResolvePathUnderRoot(currentDir, entry.FileName);
                if (File.Exists(targetPath) && HashMatches(targetPath, entry.Hash))
                    continue;

                ValidateKoreanAssetUrl(entry.Url);
                var downloadPath = PlatformHelpers.GetTempFileName();
                try
                {
                    await updater.DownloadFile(entry.Url, downloadPath, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                    if (!HashMatches(downloadPath, entry.Hash))
                        throw new DalamudIntegrityException($"Dalamud asset integrity failed for {entry.FileName}.");

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Move(downloadPath, targetPath, true);
                }
                finally
                {
                    if (File.Exists(downloadPath))
                        File.Delete(downloadPath);
                }
            }

            var devDir = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));
            PlatformHelpers.DeleteAndRecreateDirectory(devDir);
            PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
            SetLocalAssetVer(baseDir, info.Version);
            CleanUpOld(baseDir, devDir, currentDir);

            Log.Information("[DASSET] Korean assets ready at {AssetPath}", currentDir.FullName);
            return (currentDir, info.Version);
        }

        private static bool HashMatches(string filePath, string? expectedHash)
        {
            if (!File.Exists(filePath))
                return false;
            if (string.IsNullOrWhiteSpace(expectedHash))
                return true;

            using var file = File.OpenRead(filePath);
            using var sha1 = SHA1.Create();
            var actualHash = Convert.ToHexString(sha1.ComputeHash(file));
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateKoreanAssetUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/goatcorp/DalamudAssets/", StringComparison.Ordinal))
            {
                throw new DalamudIntegrityException("The Dalamud asset manifest contained an untrusted URL.");
            }
        }

        private static string GetAssetVerPath(DirectoryInfo baseDir)
        {
            return Path.Combine(baseDir.FullName, "asset.ver");
        }

        /// <summary>
        ///     Check if an asset update is needed. When this fails, just return false - the route to github
        ///     might be bad, don't wanna just bail out in that case
        /// </summary>
        /// <param name="baseDir">Base directory for assets</param>
        /// <returns>Update state</returns>
        private static async Task<(bool isRefreshNeeded, AssetInfo info)> CheckAssetRefreshNeeded(HttpClient client, DirectoryInfo baseDir)
        {
            var localVerFile = GetAssetVerPath(baseDir);
            var localVer = 0;

            try
            {
                if (File.Exists(localVerFile))
                    localVer = int.Parse(File.ReadAllText(localVerFile));
            }
            catch (Exception ex)
            {
                // This means it'll stay on 0, which will redownload all assets - good by me
                Log.Error(ex, "[DASSET] Could not read asset.ver");
            }
            

            var remoteVer = JsonSerializer.Deserialize(await client.GetStringAsync(ASSET_STORE_URL), AssetInfoJsonContext.Default.AssetInfo);

            Log.Verbose("[DASSET] Ver check - local:{0} remote:{1}", localVer, remoteVer.Version);

            var needsUpdate = remoteVer.Version > localVer;

            return (needsUpdate, remoteVer);
        }

        private static void SetLocalAssetVer(DirectoryInfo baseDir, int version)
        {
            try
            {
                var localVerFile = GetAssetVerPath(baseDir);
                File.WriteAllText(localVerFile, version.ToString());
            }
            catch (Exception e)
            {
                Log.Error(e, "[DASSET] Could not write local asset version");
            }
        }

        private static void CleanUpOld(DirectoryInfo baseDir, DirectoryInfo devDir, DirectoryInfo currentDir)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!baseDir.Exists)
                return;

            foreach (var toDelete in baseDir.GetDirectories())
            {
                if (toDelete.Name != devDir.Name && toDelete.Name != currentDir.Name)
                {
                    toDelete.Delete(true);
                    Log.Verbose("[DASSET] Cleaned out {Path}", toDelete.FullName);
                }
            }

            Log.Verbose("[DASSET] Finished cleaning");
        }
    }
    
    [JsonSerializable(typeof(AssetManager.AssetInfo))]
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    internal partial class AssetInfoJsonContext: JsonSerializerContext
    {
    }
}
