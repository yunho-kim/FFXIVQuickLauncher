using System;

namespace XIVLauncher.Common.Dalamud;

public sealed class DalamudReleaseSource
{
    public static readonly DalamudReleaseSource Global = new(
        "global", null, true, false, false);

    public static readonly DalamudReleaseSource Korean = new(
        "korean",
        "https://raw.githubusercontent.com/dal4kr/Dalamud.Resources/refs/heads/main/Dalamud/VersionInfo.json",
        false,
        true,
        true);

    private DalamudReleaseSource(
        string name,
        string? versionInfoUrl,
        bool enforceSupportedGameVersion,
        bool useOfficialMicrosoftRuntime,
        bool useKoreanAssets)
    {
        Name = name;
        VersionInfoUrl = versionInfoUrl;
        EnforceSupportedGameVersion = enforceSupportedGameVersion;
        UseOfficialMicrosoftRuntime = useOfficialMicrosoftRuntime;
        UseKoreanAssets = useKoreanAssets;
    }

    public string Name { get; }
    public string? VersionInfoUrl { get; }
    public bool EnforceSupportedGameVersion { get; }
    public bool UseOfficialMicrosoftRuntime { get; }
    public bool UseKoreanAssets { get; }

    public void ValidateDalamudDownloadUrl(string url)
    {
        if (this != Korean)
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(
                "/dal4kr/Dalamud.Resources/refs/heads/main/Dalamud/",
                StringComparison.Ordinal))
        {
            throw new DalamudIntegrityException("The Korean Dalamud manifest contained an untrusted download URL.");
        }
    }
}
