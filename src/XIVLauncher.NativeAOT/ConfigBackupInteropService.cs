using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Game.ConfigBackup;

namespace XIVLauncher.NativeAOT;

internal static class ConfigBackupInteropService
{
    public static string Export(string configPath, string destinationPath)
    {
        return Run("exported", () => FFXIVConfigArchive.Export(
            new DirectoryInfo(configPath),
            new FileInfo(destinationPath)));
    }

    public static string Import(string configPath, string sourcePath, bool preserveNewerFiles)
    {
        return Run("imported", () => FFXIVConfigArchive.Restore(
            new FileInfo(sourcePath),
            new DirectoryInfo(configPath),
            preserveNewerFiles));
    }

    private static string Run(string state, Func<FFXIVConfigArchiveResult> operation)
    {
        ConfigBackupInteropResponse response;
        try
        {
            var result = operation();
            response = ConfigBackupInteropResponse.Ok(state, result);
        }
        catch (FFXIVConfigArchiveException exception)
        {
            Log.Warning(
                "Configuration backup operation failed with {ErrorCode}",
                exception.Code);
            response = ConfigBackupInteropResponse.Error(
                exception.Code,
                exception.Message);
        }
        catch (Exception exception)
        {
            Log.Error(
                "Configuration backup operation failed unexpectedly ({ExceptionType})",
                exception.GetType().FullName);
            response = ConfigBackupInteropResponse.Error(
                "InternalError",
                "The configuration backup operation failed unexpectedly.");
        }

        return JsonSerializer.Serialize(
            response,
            ProgramJsonContext.Default.ConfigBackupInteropResponse);
    }
}

internal sealed class ConfigBackupInteropResponse
{
    public bool Success { get; set; }

    public string State { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }

    public int CharacterCount { get; set; }

    public int FileCount { get; set; }

    public int SkippedFileCount { get; set; }

    public static ConfigBackupInteropResponse Ok(
        string state,
        FFXIVConfigArchiveResult result)
    {
        return new ConfigBackupInteropResponse
        {
            Success = true,
            State = state,
            CharacterCount = result.CharacterCount,
            FileCount = result.FileCount,
            SkippedFileCount = result.SkippedFileCount,
        };
    }

    public static ConfigBackupInteropResponse Error(string errorCode, string message)
    {
        return new ConfigBackupInteropResponse
        {
            Success = false,
            State = "error",
            ErrorCode = errorCode,
            Message = message,
        };
    }
}
