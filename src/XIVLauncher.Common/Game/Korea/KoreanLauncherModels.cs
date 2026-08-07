using System;
using System.Collections.Generic;

namespace XIVLauncher.Common.Game.Korea;

public sealed class KoreanCaptchaChallenge
{
    private readonly Dictionary<string, string> form;

    internal KoreanCaptchaChallenge(Dictionary<string, string> form, Uri imageUri, byte[] imageBytes, string mediaType)
    {
        this.form = form;
        ImageUri = imageUri;
        ImageBytes = imageBytes;
        MediaType = mediaType;
    }

    public Uri ImageUri { get; }

    public byte[] ImageBytes { get; }

    public string MediaType { get; }

    internal IReadOnlyDictionary<string, string> Form => form;
}

public sealed class KoreanOtpChallenge
{
    private readonly Dictionary<string, string> sessionForm;

    internal KoreanOtpChallenge(Dictionary<string, string> sessionForm)
    {
        this.sessionForm = sessionForm;
    }

    internal IReadOnlyDictionary<string, string> SessionForm => sessionForm;
}

public sealed class KoreanAuthenticatedSession
{
    private readonly Dictionary<string, string> form;

    internal KoreanAuthenticatedSession(Dictionary<string, string> form)
    {
        this.form = form;
    }

    internal IReadOnlyDictionary<string, string> Form => form;
}

public enum KoreanLoginStatus
{
    Authenticated,
    OtpRequired,
}

public sealed class KoreanLoginResult
{
    private KoreanLoginResult(
        KoreanLoginStatus status,
        KoreanAuthenticatedSession? authenticatedSession,
        KoreanOtpChallenge? otpChallenge)
    {
        Status = status;
        AuthenticatedSession = authenticatedSession;
        OtpChallenge = otpChallenge;
    }

    public KoreanLoginStatus Status { get; }

    public KoreanAuthenticatedSession? AuthenticatedSession { get; }

    public KoreanOtpChallenge? OtpChallenge { get; }

    internal static KoreanLoginResult Authenticated(Dictionary<string, string> form)
    {
        return new KoreanLoginResult(
            KoreanLoginStatus.Authenticated,
            new KoreanAuthenticatedSession(form),
            null);
    }

    internal static KoreanLoginResult RequiresOtp(Dictionary<string, string> form)
    {
        return new KoreanLoginResult(
            KoreanLoginStatus.OtpRequired,
            null,
            new KoreanOtpChallenge(form));
    }
}
