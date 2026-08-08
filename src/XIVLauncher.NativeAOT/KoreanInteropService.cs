using System.Diagnostics;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game.Korea;
using XIVLauncher.Common.Game.Patch.PatchList;

namespace XIVLauncher.NativeAOT;

internal static class KoreanInteropService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static KoreanLauncherClient? client;
    private static KoreanCaptchaChallenge? captchaChallenge;
    private static KoreanOtpChallenge? otpChallenge;
    private static KoreanAuthenticatedSession? authenticatedSession;

    public static string PrepareLogin()
    {
        return RunSerialized(async () =>
        {
            ResetSessionCore();
            client = KoreanLauncherClient.CreateDefault();
            captchaChallenge = await client.PrepareLoginAsync(CancellationToken.None).ConfigureAwait(false);

            return KoreanInteropResponse.Ok(
                "captcha",
                login: new KoreanLoginPayload
                {
                    CaptchaImageBase64 = Convert.ToBase64String(captchaChallenge.ImageBytes),
                    CaptchaMediaType = captchaChallenge.MediaType,
                });
        });
    }

    public static string Login(string userName, string password, string captchaCode)
    {
        return RunSerialized(async () =>
        {
            if (client == null || captchaChallenge == null)
                return KoreanInteropResponse.StateError("PrepareLoginRequired");

            var result = await client.LoginAsync(
                captchaChallenge,
                userName,
                password,
                captchaCode,
                CancellationToken.None).ConfigureAwait(false);

            captchaChallenge = null;
            if (result.Status == KoreanLoginStatus.OtpRequired)
            {
                otpChallenge = result.OtpChallenge;
                authenticatedSession = null;
                return KoreanInteropResponse.Ok("otpRequired", login: new KoreanLoginPayload { OtpRequired = true });
            }

            otpChallenge = null;
            authenticatedSession = result.AuthenticatedSession;
            return KoreanInteropResponse.Ok("authenticated", login: new KoreanLoginPayload { OtpRequired = false });
        });
    }

    public static string SubmitOtp(string otp)
    {
        return RunSerialized(async () =>
        {
            if (client == null || otpChallenge == null)
                return KoreanInteropResponse.StateError("OtpChallengeRequired");

            var result = await client.SubmitOtpAsync(otpChallenge, otp, CancellationToken.None).ConfigureAwait(false);
            otpChallenge = null;
            authenticatedSession = result.AuthenticatedSession;
            return KoreanInteropResponse.Ok("authenticated", login: new KoreanLoginPayload { OtpRequired = false });
        });
    }

    public static string GetPatches()
    {
        return RunSerialized(async () =>
        {
            if (Program.Config?.GamePath == null)
                return KoreanInteropResponse.StateError("ConfigurationRequired");

            using var patchClient = KoreanPatchService.CreateHttpClient();
            var service = new KoreanPatchService(patchClient);
            var plan = await service.CheckAsync(Program.Config.GamePath, CancellationToken.None).ConfigureAwait(false);
            return KoreanInteropResponse.Ok(
                "patches",
                patch: new KoreanPatchPayload
                {
                    IsFreshInstall = plan.IsFreshInstall,
                    PendingPatches = plan.PendingPatches.ToArray(),
                });
        });
    }

    public static string StartGame(bool dalamudOk, bool noPlugins)
    {
        return RunSerialized(async () =>
        {
            if (client == null || authenticatedSession == null)
                return KoreanInteropResponse.StateError("AuthenticationRequired");

            var token = await client.CreateGameTokenAsync(authenticatedSession, CancellationToken.None).ConfigureAwait(false);
            Process process;
            try
            {
                process = LaunchServices.StartKoreanGameAndAddon(token, dalamudOk, noPlugins);
            }
            finally
            {
                ResetSessionCore();
            }

            return KoreanInteropResponse.Ok(
                "started",
                process: new DalamudConsoleOutput
                {
                    Handle = (long)process.Handle,
                    Pid = process.Id,
                });
        });
    }

    public static void ResetSession()
    {
        Gate.Wait();
        try
        {
            ResetSessionCore();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string RunSerialized(Func<Task<KoreanInteropResponse>> operation)
    {
        Gate.Wait();
        try
        {
            try
            {
                var response = operation().GetAwaiter().GetResult();
                return JsonSerializer.Serialize(response, ProgramJsonContext.Default.KoreanInteropResponse);
            }
            catch (KoreanLauncherException exception)
            {
                Log.Warning(
                    "Korean launcher operation failed at {Stage} with {Error}",
                    exception.Stage,
                    exception.Error);
                var response = KoreanInteropResponse.Error(
                    exception.Error.ToString(),
                    exception.Message,
                    exception.Stage.ToString(),
                    exception.ServerCode);
                return JsonSerializer.Serialize(response, ProgramJsonContext.Default.KoreanInteropResponse);
            }
            catch (KoreanPatchException exception)
            {
                Log.Warning(
                    "Korean patch operation failed at {Stage} with {Error}",
                    exception.Stage,
                    exception.Error);
                var response = KoreanInteropResponse.Error(
                    exception.Error.ToString(),
                    exception.Message,
                    exception.Stage);
                return JsonSerializer.Serialize(response, ProgramJsonContext.Default.KoreanInteropResponse);
            }
            catch (Exception exception)
            {
                // Runner exceptions can include the full command line, which contains
                // DEV.TestSID. Log only the type so the game token cannot reach disk.
                Log.Error(
                    "Korean launcher operation failed unexpectedly ({ExceptionType})",
                    exception.GetType().FullName);
                var response = KoreanInteropResponse.Error(
                    "InternalError",
                    "The Korean launcher operation failed unexpectedly.");
                return JsonSerializer.Serialize(response, ProgramJsonContext.Default.KoreanInteropResponse);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void ResetSessionCore()
    {
        client?.Dispose();
        client = null;
        captchaChallenge = null;
        otpChallenge = null;
        authenticatedSession = null;
    }
}

internal sealed class KoreanInteropResponse
{
    public bool Success { get; set; }

    public string State { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }

    public string? Stage { get; set; }

    public string? ServerCode { get; set; }

    public KoreanLoginPayload? Login { get; set; }

    public KoreanPatchPayload? Patch { get; set; }

    public DalamudConsoleOutput? Process { get; set; }

    public static KoreanInteropResponse Ok(
        string state,
        KoreanLoginPayload? login = null,
        KoreanPatchPayload? patch = null,
        DalamudConsoleOutput? process = null)
    {
        return new KoreanInteropResponse
        {
            Success = true,
            State = state,
            Login = login,
            Patch = patch,
            Process = process,
        };
    }

    public static KoreanInteropResponse StateError(string errorCode)
    {
        return Error(errorCode, "The Korean launcher operation was called in an invalid state.");
    }

    public static KoreanInteropResponse Error(
        string errorCode,
        string message,
        string? stage = null,
        string? serverCode = null)
    {
        return new KoreanInteropResponse
        {
            Success = false,
            State = "error",
            ErrorCode = errorCode,
            Message = message,
            Stage = stage,
            ServerCode = serverCode,
        };
    }
}

internal sealed class KoreanLoginPayload
{
    public string? CaptchaImageBase64 { get; set; }

    public string? CaptchaMediaType { get; set; }

    public bool OtpRequired { get; set; }
}

internal sealed class KoreanPatchPayload
{
    public bool IsFreshInstall { get; set; }

    public PatchListEntry[] PendingPatches { get; set; } = Array.Empty<PatchListEntry>();
}
