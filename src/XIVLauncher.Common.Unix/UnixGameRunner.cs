using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix.Compatibility;

namespace XIVLauncher.Common.Unix;

public class UnixGameRunner : IArgumentListGameRunner
{
    private readonly CompatibilityTools compatibility;
    private readonly DalamudLauncher dalamudLauncher;
    private readonly bool dalamudOk;

    public UnixGameRunner(CompatibilityTools compatibility, DalamudLauncher dalamudLauncher, bool dalamudOk)
    {
        this.compatibility = compatibility;
        this.dalamudLauncher = dalamudLauncher;
        this.dalamudOk = dalamudOk;
    }

    public Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness)
    {
        if (dalamudOk)
        {
            return this.dalamudLauncher.Run(new FileInfo(path), arguments, environment);
        }
        else
        {
            // XIV on Mac layers its native DXMT d3d11/dxgi modules over Wine.
            // Keep the default native d3d11 load order used by the upstream app.
            return compatibility.RunInPrefix($"\"{path}\" {arguments}", workingDirectory, environment, writeLog: true);
        }
    }

    public Process? Start(
        string path,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IDictionary<string, string> environment,
        DpiAwareness dpiAwareness)
    {
        // The Korean client uses an unencrypted UserPath argument. Passing a
        // command-line string makes paths containing spaces depend on two
        // separate parsers (.NET and Wine). ArgumentList keeps every key/value
        // pair intact all the way to Wine. Dalamud is intentionally disabled
        // for Korean launches, so this structured path starts Wine directly.
        var wineArguments = new string[arguments.Count + 1];
        wineArguments[0] = path;
        for (var i = 0; i < arguments.Count; i++)
            wineArguments[i + 1] = arguments[i];

        return compatibility.RunInPrefix(
            wineArguments,
            workingDirectory,
            environment,
            writeLog: true);
    }
}
