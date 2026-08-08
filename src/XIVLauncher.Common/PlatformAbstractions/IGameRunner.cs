using System.Collections.Generic;
using System.Diagnostics;

namespace XIVLauncher.Common.PlatformAbstractions;

public interface IGameRunner
{
    Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DpiAwareness dpiAwareness);
}

/// <summary>
/// A game runner that can pass already-separated arguments to the operating
/// system without reparsing a command-line string.
/// </summary>
public interface IArgumentListGameRunner : IGameRunner
{
    Process? Start(
        string path,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IDictionary<string, string> environment,
        DpiAwareness dpiAwareness);
}
