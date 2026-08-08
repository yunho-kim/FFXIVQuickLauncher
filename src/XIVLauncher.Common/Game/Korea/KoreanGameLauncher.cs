using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Game.Korea;

public sealed class KoreanGameLauncher
{
    private const string LobbyHost = "nlobbyf-live.ff14.co.kr";
    private const int LobbyPort = 54994;
    private const string GmServerHost = "ngm-live.ff14.co.kr";
    private const string SaveDataBankHost = "nconfig-dl-live.ff14.co.kr";

    private static readonly Regex AdditionalArgumentRegex = new(
        @"\s*(?<key>[^\s=]+)\s*=\s*(?<value>([^=]*$|[^=]*\s(?=[^\s=]+)))\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ProtectedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEV.LobbyHost01",
        "DEV.LobbyPort01",
        "DEV.GMServerHost",
        "DEV.TestSID",
        "SYS.resetConfig",
        "DEV.SaveDataBankHost",
    };

    public Process? LaunchGame(
        IGameRunner runner,
        string gameToken,
        string additionalArguments,
        DirectoryInfo gamePath,
        DpiAwareness dpiAwareness)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameToken);
        ArgumentNullException.ThrowIfNull(gamePath);

        var executablePath = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");
        if (!File.Exists(executablePath))
            throw new BinaryNotPresentException(executablePath);

        var argumentBuilder = new ArgumentBuilder();
        if (!string.IsNullOrWhiteSpace(additionalArguments))
        {
            foreach (Match match in AdditionalArgumentRegex.Matches(additionalArguments))
            {
                var key = match.Groups["key"].Value;
                if (!ProtectedArguments.Contains(key))
                    argumentBuilder.Append(key, match.Groups["value"].Value.Trim());
            }
        }

        argumentBuilder
            .Append("DEV.LobbyHost01", LobbyHost)
            .Append("DEV.LobbyPort01", LobbyPort.ToString(CultureInfo.InvariantCulture))
            .Append("DEV.GMServerHost", GmServerHost)
            .Append("DEV.TestSID", gameToken)
            .Append("SYS.resetConfig", "0")
            .Append("DEV.SaveDataBankHost", SaveDataBankHost);

        var workingDirectory = Path.Combine(gamePath.FullName, "game");
        var environment = new Dictionary<string, string>();
        if (runner is IArgumentListGameRunner argumentListRunner)
        {
            return argumentListRunner.Start(
                executablePath,
                workingDirectory,
                argumentBuilder.BuildArgumentList(),
                environment,
                dpiAwareness);
        }

        return runner.Start(
            executablePath,
            workingDirectory,
            argumentBuilder.Build(),
            environment,
            dpiAwareness);
    }
}
