using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Korea;

namespace XIVLauncher.Common.Tests.Game.Korea;

[TestClass]
public class KoreanLauncherClientTests
{
    private const string PasswordSentinel = "password-sentinel";
    private const string CaptchaSentinel = "abcde";
    private const string OtpSentinel = "otp-sentinel";

    [TestMethod]
    public async Task PrepareLoginDownloadsCaptchaThroughEstablishedSession()
    {
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html", sessionCookie: "launcher-session=fixture")),
            request =>
            {
                Assert.AreEqual("/BotDetectCaptcha.ashx?get=image&c=LauncherLoginCaptcha&t=fixture-token", request.RequestUri!.PathAndQuery);
                return Task.FromResult(CaptchaResponse());
            });

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            challenge.ImageBytes);
        Assert.AreEqual(new Uri("https://newlauncher.ff14.co.kr/BotDetectCaptcha.ashx?get=image&c=LauncherLoginCaptcha&t=fixture-token"), challenge.ImageUri);
    }

    [TestMethod]
    public async Task DefaultSessionFactorySendsCookieOnEveryAuthenticationRequest()
    {
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        using var listener = new HttpListener();
        var baseUri = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(baseUri.AbsoluteUri);
        listener.Start();
        var cookies = new List<string?>();

        var server = Task.Run(async () =>
        {
            for (var requestIndex = 0; requestIndex < 5; requestIndex++)
            {
                var context = await listener.GetContextAsync();
                if (requestIndex > 0)
                    cookies.Add(context.Request.Cookies["launcher-session"]?.Value);

                byte[] body;
                string contentType;
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/":
                        context.Response.Headers.Add("Set-Cookie", "launcher-session=fixture; Path=/; HttpOnly");
                        body = Encoding.UTF8.GetBytes(FixtureText("launcher-login.html"));
                        contentType = "text/html";
                        break;
                    case "/BotDetectCaptcha.ashx":
                        body = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                        contentType = "image/png";
                        break;
                    case "/LauncherFF/LauncherProcess":
                        body = Encoding.UTF8.GetBytes(FixtureText("login-otp-required.json"));
                        contentType = "application/json";
                        break;
                    case "/LauncherFF/OTPCheck":
                        body = Encoding.UTF8.GetBytes(FixtureText("otp-success.json"));
                        contentType = "application/json";
                        break;
                    case "/LauncherFF/MakeToken":
                        body = Encoding.UTF8.GetBytes(FixtureText("token-success.json"));
                        contentType = "application/json";
                        break;
                    default:
                        throw new AssertFailedException($"Unexpected request path: {context.Request.Url.AbsolutePath}");
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = contentType;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });

        using var client = KoreanLauncherClient.Create(baseUri);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var login = await client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None);
        var authenticated = await client.SubmitOtpAsync(login.OtpChallenge!, OtpSentinel, CancellationToken.None);
        var token = await client.CreateGameTokenAsync(authenticated.AuthenticatedSession!, CancellationToken.None);
        await server;

        Assert.AreEqual("fixture-game-token", token);
        CollectionAssert.AreEqual(
            new string?[] { "fixture", "fixture", "fixture", "fixture" },
            cookies);
    }

    [TestMethod]
    public async Task LoginPostsCredentialsAndPreservesParsedFormFields()
    {
        Dictionary<string, string>? postedForm = null;
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            async request =>
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("/LauncherFF/LauncherProcess", request.RequestUri!.AbsolutePath);
                postedForm = await ReadFormAsync(request);
                return Response("login-success.json");
            });

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var result = await client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None);

        Assert.AreEqual(KoreanLoginStatus.Authenticated, result.Status);
        Assert.IsNotNull(result.AuthenticatedSession);
        Assert.AreEqual("34", postedForm!["gameServiceID"]);
        Assert.AreEqual("fixture-token", postedForm["BDC_VCID_LauncherLoginCaptcha"]);
        Assert.AreEqual("fixture-hash", postedForm["BDC_Hs_LauncherLoginCaptcha"]);
        Assert.AreEqual("1", postedForm["BDC_BackWorkaround_LauncherLoginCaptcha"]);
        Assert.AreEqual("true", postedForm["setting_update"]);
        Assert.IsFalse(postedForm.ContainsKey("setting_dx11"));
        Assert.IsFalse(postedForm.ContainsKey("setting_reset"));
        Assert.IsFalse(postedForm.ContainsKey("disabled_setting"));
        Assert.AreEqual("fixture-user", postedForm["memberID"]);
        Assert.AreEqual(PasswordSentinel, postedForm["passWord"]);
        Assert.AreEqual(CaptchaSentinel, postedForm["CaptchaCode"]);
    }

    [TestMethod]
    public async Task InvalidCaptchaThrowsSafeTypedException()
    {
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            Step(Response("login-captcha-error.json")));

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidCaptcha, exception.Error);
        Assert.AreEqual(KoreanLauncherStage.Login, exception.Stage);
        Assert.AreEqual("-106", exception.ServerCode);
        Assert.IsFalse(exception.ToString().Contains(PasswordSentinel, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(CaptchaSentinel, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("response-body-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TokenCreationNormalizesAuthenticatedFormForGameSession()
    {
        Dictionary<string, string>? otpForm = null;
        Dictionary<string, string>? tokenForm = null;
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            Step(Response("login-otp-required.json")),
            async request =>
            {
                Assert.AreEqual("/LauncherFF/OTPCheck", request.RequestUri!.AbsolutePath);
                otpForm = await ReadFormAsync(request);
                return Response("otp-success.json");
            },
            async request =>
            {
                Assert.AreEqual("/LauncherFF/MakeToken", request.RequestUri!.AbsolutePath);
                tokenForm = await ReadFormAsync(request);
                return Response("token-success.json");
            });

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var login = await client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None);

        Assert.AreEqual(KoreanLoginStatus.OtpRequired, login.Status);
        var authenticated = await client.SubmitOtpAsync(login.OtpChallenge!, OtpSentinel, CancellationToken.None);
        var token = await client.CreateGameTokenAsync(authenticated.AuthenticatedSession!, CancellationToken.None);

        Assert.AreEqual("fixture-game-token", token);
        Assert.AreEqual("fixture-motp-id", otpForm!["motpID"]);
        Assert.AreEqual(OtpSentinel, otpForm["otpNum"]);
        Assert.AreEqual("fixture-member-key", otpForm["memberKey"]);
        Assert.AreEqual("fixture-user", otpForm["memberID"]);
        Assert.IsFalse(otpForm.ContainsKey("passWord"));
        Assert.IsFalse(otpForm.ContainsKey("CaptchaCode"));
        Assert.AreEqual("fixture-member-key", tokenForm!["memberKey"]);
        Assert.AreEqual("fixture-user", tokenForm["memberID"]);
        Assert.AreEqual("0", tokenForm["InternetCafeType"]);
        Assert.AreEqual(string.Empty, tokenForm["passWord"]);
        Assert.AreEqual(CaptchaSentinel, tokenForm["CaptchaCode"]);
        Assert.AreEqual("1", tokenForm["decideDX"]);
        Assert.AreEqual("1", tokenForm["decideAS"]);
        Assert.AreEqual("true", tokenForm["checkMemberID"]);
        Assert.AreEqual(OtpSentinel, tokenForm["otpNum"]);
        Assert.IsFalse(tokenForm.Values.Contains(PasswordSentinel));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "gameServiceID", "csiteNo", "isPcBang", "InternetCafeType", "hid_freeTrial",
                "hid_freeTrialRemainDate", "cancelFlag", "chNppAuth", "resetSetting", "setting_update",
                "BDC_VCID_LauncherLoginCaptcha",
                "BDC_BackWorkaround_LauncherLoginCaptcha", "BDC_Hs_LauncherLoginCaptcha",
                "BDC_SP_LauncherLoginCaptcha", "memberID", "passWord", "CaptchaCode", "checkMemberID",
                "memberKey", "motpID", "otpNum", "decideDX", "decideAS",
            },
            tokenForm.Keys.ToArray());
    }

    [TestMethod]
    public async Task EmptyMotpIdIsForwardedToOtpCheck()
    {
        const string loginResponse =
            """{"result":"0","loginResult":"O","motpUse":"O","motpID":"","memberID":"fixture-user","memberKey":"fixture-key","csiteNo":"0"}""";
        Dictionary<string, string>? otpForm = null;
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            Step(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(loginResponse, Encoding.UTF8, "application/json"),
            }),
            async request =>
            {
                Assert.AreEqual("/LauncherFF/OTPCheck", request.RequestUri!.AbsolutePath);
                otpForm = await ReadFormAsync(request);
                return Response("otp-success.json");
            });

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var login = await client.LoginAsync(
            challenge,
            "fixture-user",
            PasswordSentinel,
            CaptchaSentinel,
            CancellationToken.None);
        var authenticated = await client.SubmitOtpAsync(
            login.OtpChallenge!,
            OtpSentinel,
            CancellationToken.None);

        Assert.AreEqual(KoreanLoginStatus.Authenticated, authenticated.Status);
        Assert.AreEqual(string.Empty, otpForm!["motpID"]);
        Assert.AreEqual(OtpSentinel, otpForm["otpNum"]);
        Assert.AreEqual("fixture-key", otpForm["memberKey"]);
        Assert.AreEqual("fixture-user", otpForm["memberID"]);
    }

    [DataTestMethod]
    [DataRow("""{"result":"0","loginResult":"O","memberID":"fixture-user","memberKey":"fixture-key","csiteNo":"0"}""")]
    [DataRow("""{"result":"0","loginResult":"O","motpUse":"unknown","memberID":"fixture-user","memberKey":"fixture-key","csiteNo":"0"}""")]
    [DataRow("""{"result":"0","loginResult":"O","motpUse":"X","memberKey":"fixture-key","csiteNo":"0"}""")]
    [DataRow("""{"result":"0","loginResult":"O","motpUse":"X","memberID":"fixture-user","csiteNo":"0"}""")]
    [DataRow("""{"result":"0","loginResult":"O","motpUse":"X","memberID":"fixture-user","memberKey":"fixture-key"}""")]
    [DataRow("""{"result":"0","loginResult":"O","motpUse":[],"memberID":"fixture-user","memberKey":"fixture-key","csiteNo":"0"}""")]
    public async Task IncompleteSuccessfulLoginResponseIsRejected(string responseBody)
    {
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            Step(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            }));

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidResponse, exception.Error);
        Assert.AreEqual(KoreanLauncherStage.Login, exception.Stage);
    }

    [TestMethod]
    public async Task TimeoutIsReportedAsNetworkFailure()
    {
        var handler = new FixtureHandler(_ => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true)));

        using var client = CreateClient(handler);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.PrepareLoginAsync(CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.NetworkFailure, exception.Error);
        Assert.AreEqual(KoreanLauncherStage.PrepareLogin, exception.Stage);
    }

    [TestMethod]
    public async Task CallerCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new FixtureHandler(
            request => Task.FromCanceled<HttpResponseMessage>(cancellation.Token));

        using var client = CreateClient(handler);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => client.PrepareLoginAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task CaptchaHtmlResponseIsRejected()
    {
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not-an-image</html>", Encoding.UTF8, "text/html"),
            }));

        using var client = CreateClient(handler);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.PrepareLoginAsync(CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidResponse, exception.Error);
    }

    [TestMethod]
    public async Task OversizedCaptchaIsRejected()
    {
        var oversized = new byte[1024 * 1024 + 1];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(oversized, 0);
        var content = new ByteArrayContent(oversized);
        content.Headers.ContentType = new("image/png");
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        using var client = CreateClient(handler);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.PrepareLoginAsync(CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidResponse, exception.Error);
    }

    [TestMethod]
    public async Task InvalidCaptchaSignatureIsRejected()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("not a png"));
        content.Headers.ContentType = new("image/png");
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        using var client = CreateClient(handler);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.PrepareLoginAsync(CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidResponse, exception.Error);
    }

    [TestMethod]
    public async Task ExpiredTokenSessionThrowsSafeTypedException()
    {
        const string expiredResponse = """{"tokenResult":"-401","toKen":"response-token-secret"}""";
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            Step(Response("login-success.json")),
            Step(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(expiredResponse, Encoding.UTF8, "application/json") }));

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var login = await client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.CreateGameTokenAsync(login.AuthenticatedSession!, CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.SessionExpired, exception.Error);
        Assert.AreEqual(KoreanLauncherStage.Token, exception.Stage);
        Assert.IsFalse(exception.ToString().Contains("response-token-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingCaptchaMarkupThrowsDocumentChangedWithoutIncludingHtml()
    {
        const string malformedHtml = "<html>malformed-html-secret</html>";
        var handler = new FixtureHandler(
            Step(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(malformedHtml) }));

        using var client = CreateClient(handler);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.PrepareLoginAsync(CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidResponse, exception.Error);
        Assert.AreEqual(KoreanLauncherStage.PrepareLogin, exception.Stage);
        Assert.IsFalse(exception.ToString().Contains("malformed-html-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnknownServerCodeIsNotCopiedIntoException()
    {
        const string responseWithSecretCode = """{"result":"server-code-secret"}""";
        var handler = new FixtureHandler(
            Step(Response("launcher-login.html")),
            Step(CaptchaResponse()),
            Step(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseWithSecretCode, Encoding.UTF8, "application/json"),
            }));

        using var client = CreateClient(handler);
        var challenge = await client.PrepareLoginAsync(CancellationToken.None);
        var exception = await Assert.ThrowsExceptionAsync<KoreanLauncherException>(
            () => client.LoginAsync(challenge, "fixture-user", PasswordSentinel, CaptchaSentinel, CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.AuthenticationFailed, exception.Error);
        Assert.IsNull(exception.ServerCode);
        Assert.IsFalse(exception.ToString().Contains("server-code-secret", StringComparison.Ordinal));
    }

    private static KoreanLauncherClient CreateClient(HttpMessageHandler handler)
    {
        return new KoreanLauncherClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://newlauncher.ff14.co.kr/"),
        });
    }

    private static HttpResponseMessage CaptchaResponse()
    {
        var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        content.Headers.ContentType = new("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static HttpResponseMessage Response(string fixtureName, string? sessionCookie = null, [CallerFilePath] string sourcePath = "")
    {
        var fixturePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "Fixtures", fixtureName);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(fixturePath), Encoding.UTF8, fixtureName.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "text/html"),
        };
        if (sessionCookie != null)
            response.Headers.Add("Set-Cookie", sessionCookie);
        return response;
    }

    private static string FixtureText(string fixtureName, [CallerFilePath] string sourcePath = "")
    {
        return File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourcePath)!, "Fixtures", fixtureName));
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpRequestMessage request)
    {
        var pairs = await request.Content!.ReadAsStringAsync();
        return pairs.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Step(HttpResponseMessage response)
    {
        return _ => Task.FromResult(response);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> responses;

        public FixtureHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses)
        {
            this.responses = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.IsTrue(responses.Count > 0, $"Unexpected request: {request.Method} {request.RequestUri}");
            return await responses.Dequeue()(request);
        }
    }
}
