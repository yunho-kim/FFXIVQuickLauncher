using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Game.Korea;

public sealed class KoreanLauncherClient : IDisposable
{
    private const int MaxCaptchaImageBytes = 1024 * 1024;

    public static readonly Uri LauncherBaseUri = new("https://newlauncher.ff14.co.kr/");

    private static readonly Regex InputTagRegex = new(
        @"<input\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex ImageTagRegex = new(
        @"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SetTypeRegex = new(
        @"externalFN\s*\(\s*[""']SetType[""']\s*,\s*\{\s*nType\s*:\s*(?<value>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeRegex = new(
        @"(?<name>[\w:-]+)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly string[] RequiredCaptchaFields =
    {
        "BDC_VCID_LauncherLoginCaptcha",
        "BDC_BackWorkaround_LauncherLoginCaptcha",
        "BDC_Hs_LauncherLoginCaptcha",
        "BDC_SP_LauncherLoginCaptcha",
    };

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegSignature = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] Gif87Signature = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a' };
    private static readonly byte[] Gif89Signature = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' };

    private static readonly HashSet<string> SafeServerCodes = new(StringComparer.Ordinal)
    {
        "-101", "-102", "-106", "-107", "-401", "-909", "601",
        "C", "D", "E", "K", "L", "N", "P", "Q", "S", "T", "V", "W", "Z",
        "ip_restricted", "otp_rejected",
    };

    private readonly HttpClient client;

    public KoreanLauncherClient(HttpClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.client.BaseAddress ??= LauncherBaseUri;
        ValidateBaseAddress(this.client.BaseAddress);
    }

    public static KoreanLauncherClient CreateDefault()
    {
        return Create(LauncherBaseUri);
    }

    public static KoreanLauncherClient Create(Uri launcherBaseUri)
    {
        ArgumentNullException.ThrowIfNull(launcherBaseUri);
        ValidateBaseAddress(launcherBaseUri);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = launcherBaseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIV-on-Mac-KR");
        return new KoreanLauncherClient(client);
    }

    public async Task<KoreanCaptchaChallenge> PrepareLoginAsync(CancellationToken cancellationToken)
    {
        using var pageResponse = await SendAsync(
            () => client.GetAsync(string.Empty, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
            KoreanLauncherStage.PrepareLogin,
            cancellationToken).ConfigureAwait(false);
        var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match tag in InputTagRegex.Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);
            if (!attributes.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                continue;

            attributes.TryGetValue("value", out var value);
            form[name] = value ?? string.Empty;
        }

        if (!form.ContainsKey("gameServiceID")
            || !form.ContainsKey("csiteNo")
            || RequiredCaptchaFields.Any(field => !form.ContainsKey(field)))
        {
            throw InvalidResponse(KoreanLauncherStage.PrepareLogin);
        }

        var setTypeMatch = SetTypeRegex.Match(html);
        if (!setTypeMatch.Success
            || !int.TryParse(
                setTypeMatch.Groups["value"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var launcherType))
        {
            throw InvalidResponse(KoreanLauncherStage.PrepareLogin);
        }

        form["InternetCafeType"] = launcherType > 0
            ? (launcherType % 2).ToString(CultureInfo.InvariantCulture)
            : "0";
        form["BDC_BackWorkaround_LauncherLoginCaptcha"] = "1";

        Uri? imageUri = null;
        foreach (Match tag in ImageTagRegex.Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);
            if (!attributes.TryGetValue("id", out var id)
                || !string.Equals(id, "LauncherLoginCaptcha_CaptchaImage", StringComparison.Ordinal)
                || !attributes.TryGetValue("src", out var source)
                || !Uri.TryCreate(client.BaseAddress, source, out imageUri))
            {
                continue;
            }

            break;
        }

        if (imageUri == null || !IsSameOrigin(client.BaseAddress!, imageUri))
            throw InvalidResponse(KoreanLauncherStage.PrepareLogin);

        using var imageResponse = await SendAsync(
            () => client.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
            KoreanLauncherStage.PrepareLogin,
            cancellationToken).ConfigureAwait(false);
        var finalImageUri = imageResponse.RequestMessage?.RequestUri;
        if (finalImageUri != null && !IsSameOrigin(client.BaseAddress!, finalImageUri))
            throw InvalidResponse(KoreanLauncherStage.PrepareLogin);

        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("image/png" or "image/jpeg" or "image/gif")
            || imageResponse.Content.Headers.ContentLength > MaxCaptchaImageBytes)
        {
            throw InvalidResponse(KoreanLauncherStage.PrepareLogin);
        }

        await using var imageStream = await imageResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var imageBuffer = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await imageStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (imageBuffer.Length + read > MaxCaptchaImageBytes)
                throw InvalidResponse(KoreanLauncherStage.PrepareLogin);
            imageBuffer.Write(buffer, 0, read);
        }

        var imageBytes = imageBuffer.ToArray();
        var hasValidSignature = mediaType switch
        {
            "image/png" => imageBytes.AsSpan().StartsWith(PngSignature),
            "image/jpeg" => imageBytes.AsSpan().StartsWith(JpegSignature),
            "image/gif" => imageBytes.AsSpan().StartsWith(Gif87Signature) || imageBytes.AsSpan().StartsWith(Gif89Signature),
            _ => false,
        };
        if (!hasValidSignature)
            throw InvalidResponse(KoreanLauncherStage.PrepareLogin);

        return new KoreanCaptchaChallenge(form, imageUri, imageBytes, mediaType);
    }

    public async Task<KoreanLoginResult> LoginAsync(
        KoreanCaptchaChallenge challenge,
        string userName,
        string password,
        string captchaCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var form = new Dictionary<string, string>(challenge.Form, StringComparer.Ordinal)
        {
            ["memberID"] = userName,
            ["passWord"] = password,
            ["CaptchaCode"] = captchaCode,
            ["checkMemberID"] = "false",
        };

        using var response = await PostFormAsync(
            "LauncherFF/LauncherProcess",
            form,
            KoreanLauncherStage.Login,
            cancellationToken).ConfigureAwait(false);
        using var json = await ParseJsonAsync(response, KoreanLauncherStage.Login, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var resultCode = GetOptionalScalar(root, "result", KoreanLauncherStage.Login);
        if (resultCode == null)
            throw InvalidResponse(KoreanLauncherStage.Login);
        if (!string.Equals(resultCode, "0", StringComparison.Ordinal))
        {
            var error = resultCode switch
            {
                "-106" => KoreanLauncherError.InvalidCaptcha,
                "-102" or "-107" => KoreanLauncherError.InvalidCredentials,
                "-101" => KoreanLauncherError.AccountRestricted,
                _ => KoreanLauncherError.AuthenticationFailed,
            };
            throw AuthenticationException(KoreanLauncherStage.Login, error, resultCode);
        }

        if (string.Equals(GetOptionalScalar(root, "ipUseChek", KoreanLauncherStage.Login), "X", StringComparison.OrdinalIgnoreCase))
            throw AuthenticationException(KoreanLauncherStage.Login, KoreanLauncherError.AccountRestricted, "ip_restricted");

        var loginCode = GetOptionalScalar(root, "loginResult", KoreanLauncherStage.Login);
        if (loginCode == null)
            throw InvalidResponse(KoreanLauncherStage.Login);
        if (!string.Equals(loginCode, "O", StringComparison.OrdinalIgnoreCase))
        {
            var error = loginCode is "T" or "E" or "C" or "P"
                ? KoreanLauncherError.InvalidCredentials
                : KoreanLauncherError.AccountRestricted;
            throw AuthenticationException(KoreanLauncherStage.Login, error, loginCode);
        }

        var sessionForm = new Dictionary<string, string>(form, StringComparer.Ordinal);

        var motpUse = GetRequiredScalar(root, "motpUse", KoreanLauncherStage.Login);
        if (motpUse is not ("O" or "X"))
            throw InvalidResponse(KoreanLauncherStage.Login);

        foreach (var requiredField in new[] { "memberID", "memberKey", "csiteNo" })
        {
            var value = GetRequiredScalar(root, requiredField, KoreanLauncherStage.Login);
            if (string.IsNullOrWhiteSpace(value))
                throw InvalidResponse(KoreanLauncherStage.Login);
            sessionForm[requiredField] = value;
        }

        if (motpUse == "O")
        {
            // Some valid U-OTP accounts return an empty motpID. The official
            // launcher forwards that value to OTPCheck unchanged.
            if (GetOptionalScalar(root, "motpID", KoreanLauncherStage.Login) is { } motpId)
                sessionForm["motpID"] = motpId;
        }

        var freeTrial = GetOptionalScalar(root, "freeTrial", KoreanLauncherStage.Login);
        if (freeTrial != null)
            sessionForm["hid_freeTrial"] = freeTrial;
        var freeTrialRemainDate = GetOptionalScalar(root, "freeTrialRemainDate", KoreanLauncherStage.Login);
        if (freeTrialRemainDate != null)
            sessionForm["hid_freeTrialRemainDate"] = freeTrialRemainDate;

        return motpUse == "O"
            ? KoreanLoginResult.RequiresOtp(sessionForm)
            : KoreanLoginResult.Authenticated(sessionForm);
    }

    public async Task<KoreanLoginResult> SubmitOtpAsync(
        KoreanOtpChallenge challenge,
        string otp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var sessionForm = new Dictionary<string, string>(challenge.SessionForm, StringComparer.Ordinal);
        var otpForm = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["motpID"] = sessionForm.GetValueOrDefault("motpID", string.Empty),
            ["otpNum"] = otp,
            ["csiteNo"] = sessionForm.GetValueOrDefault("csiteNo", "0"),
            ["memberKey"] = sessionForm.GetValueOrDefault("memberKey", string.Empty),
            ["memberID"] = sessionForm.GetValueOrDefault("memberID", string.Empty),
        };

        using var response = await PostFormAsync(
            "LauncherFF/OTPCheck",
            otpForm,
            KoreanLauncherStage.Otp,
            cancellationToken).ConfigureAwait(false);
        using var json = await ParseJsonAsync(response, KoreanLauncherStage.Otp, cancellationToken).ConfigureAwait(false);
        var otpResult = GetOptionalScalar(json.RootElement, "Result", KoreanLauncherStage.Otp);
        if (otpResult == null)
            throw InvalidResponse(KoreanLauncherStage.Otp);
        if (otpResult.Length != 0)
            throw AuthenticationException(KoreanLauncherStage.Otp, KoreanLauncherError.OtpRejected, "otp_rejected");

        return KoreanLoginResult.Authenticated(sessionForm);
    }

    public async Task<string> CreateGameTokenAsync(
        KoreanAuthenticatedSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var response = await PostFormAsync(
            "LauncherFF/MakeToken",
            session.Form,
            KoreanLauncherStage.Token,
            cancellationToken).ConfigureAwait(false);
        using var json = await ParseJsonAsync(response, KoreanLauncherStage.Token, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var tokenResult = GetOptionalScalar(root, "tokenResult", KoreanLauncherStage.Token);
        if (tokenResult == null)
            throw InvalidResponse(KoreanLauncherStage.Token);
        if (!string.Equals(tokenResult, "0", StringComparison.Ordinal))
        {
            var error = tokenResult switch
            {
                "-401" => KoreanLauncherError.SessionExpired,
                "-909" => KoreanLauncherError.Maintenance,
                _ => KoreanLauncherError.AuthenticationFailed,
            };
            throw AuthenticationException(KoreanLauncherStage.Token, error, tokenResult);
        }

        var token = GetOptionalScalar(root, "toKen", KoreanLauncherStage.Token);
        if (string.IsNullOrEmpty(token))
            throw InvalidResponse(KoreanLauncherStage.Token);
        return token;
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string relativeUri,
        IReadOnlyDictionary<string, string> form,
        KoreanLauncherStage stage,
        CancellationToken cancellationToken)
    {
        return await SendAsync(
            () => client.PostAsync(relativeUri, new FormUrlEncodedContent(form), cancellationToken),
            stage,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ParseJsonAsync(
        HttpResponseMessage response,
        KoreanLauncherStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            throw InvalidResponse(stage);
        }
        catch (JsonException)
        {
            throw InvalidResponse(stage);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        KoreanLauncherStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await send().ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return response;

            response.Dispose();
            throw new KoreanLauncherException(
                stage,
                KoreanLauncherError.NetworkFailure,
                "The Korean launcher service returned an unsuccessful HTTP status.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new KoreanLauncherException(
                stage,
                KoreanLauncherError.NetworkFailure,
                "The Korean launcher service request timed out.",
                innerException: exception);
        }
        catch (KoreanLauncherException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new KoreanLauncherException(
                stage,
                KoreanLauncherError.NetworkFailure,
                "The Korean launcher service could not be reached.",
                innerException: exception);
        }
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex.Matches(tag))
        {
            var value = attribute.Groups["double"].Success
                ? attribute.Groups["double"].Value
                : attribute.Groups["single"].Success
                    ? attribute.Groups["single"].Value
                    : attribute.Groups["bare"].Value;
            attributes[attribute.Groups["name"].Value] = WebUtility.HtmlDecode(value);
        }

        return attributes;
    }

    private static string GetRequiredScalar(JsonElement json, string propertyName, KoreanLauncherStage stage)
    {
        return GetOptionalScalar(json, propertyName, stage) ?? throw InvalidResponse(stage);
    }

    private static string? GetOptionalScalar(JsonElement json, string propertyName, KoreanLauncherStage stage)
    {
        JsonElement value = default;
        var found = false;
        foreach (var property in json.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
            break;
        }

        if (!found || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => throw InvalidResponse(stage),
        };
    }

    private static bool IsSameOrigin(Uri first, Uri second)
    {
        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase)
               && first.Port == second.Port;
    }

    private static void ValidateBaseAddress(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Korean launcher base address must use HTTPS.", nameof(uri));
    }

    private static KoreanLauncherException InvalidResponse(KoreanLauncherStage stage)
    {
        return new KoreanLauncherException(
            stage,
            KoreanLauncherError.InvalidResponse,
            "The Korean launcher response format was not recognized.");
    }

    private static KoreanLauncherException AuthenticationException(
        KoreanLauncherStage stage,
        KoreanLauncherError error,
        string? serverCode)
    {
        return new KoreanLauncherException(
            stage,
            error,
            error switch
            {
                KoreanLauncherError.InvalidCaptcha => "The CAPTCHA value was rejected.",
                KoreanLauncherError.InvalidCredentials => "The account credentials were rejected.",
                KoreanLauncherError.AccountRestricted => "The account cannot log in through the launcher.",
                KoreanLauncherError.OtpRejected => "The one-time password was rejected.",
                KoreanLauncherError.SessionExpired => "The launcher session expired.",
                KoreanLauncherError.Maintenance => "The Korean game service is under maintenance.",
                _ => "The Korean launcher authentication request failed.",
            },
            serverCode != null && SafeServerCodes.Contains(serverCode) ? serverCode : null);
    }
}
