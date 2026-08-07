using System;

namespace XIVLauncher.Common.Game.Korea;

public enum KoreanLauncherStage
{
    PrepareLogin,
    Login,
    Otp,
    Token,
}

public enum KoreanLauncherError
{
    NetworkFailure,
    InvalidResponse,
    InvalidCaptcha,
    InvalidCredentials,
    AccountRestricted,
    OtpRejected,
    SessionExpired,
    AuthenticationFailed,
    Maintenance,
}

public sealed class KoreanLauncherException : Exception
{
    internal KoreanLauncherException(
        KoreanLauncherStage stage,
        KoreanLauncherError error,
        string message,
        string? serverCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        Error = error;
        ServerCode = serverCode;
    }

    public KoreanLauncherStage Stage { get; }

    public KoreanLauncherError Error { get; }

    public string? ServerCode { get; }
}
