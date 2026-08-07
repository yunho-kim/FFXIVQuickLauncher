using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.Korea;

namespace XIVLauncher.Common.Tests.Game.Korea;

[TestClass]
public sealed class KoreanLauncherClientTests
{
    private const string LoginHtml = """
        <html><body><form>
        <input value="34" name="gameServiceID">
        <input value="0" name="csiteNo">
        <input value="fixture-token" name="BDC_VCID_LauncherLoginCaptcha">
        <input value="0" name="BDC_BackWorkaround_LauncherLoginCaptcha">
        <input value="fixture-hash" name="BDC_Hs_LauncherLoginCaptcha">
        <input value="262452672" name="BDC_SP_LauncherLoginCaptcha">
        <img id="LauncherLoginCaptcha_CaptchaImage" src="/captcha.png">
        </form><script>externalFN("SetType", { nType : 689014 });</script></body></html>
        """;

    [TestMethod]
    public async Task LoginOtpAndTokenUseOneValidatedSession()
    {
        Dictionary<string, string>? loginForm = null;
        Dictionary<string, string>? tokenForm = null;
        var handler = new FixtureHandler(
            _ => TextResponse(LoginHtml, "text/html"),
            _ => CaptchaResponse(),
            async request =>
            {
                loginForm = await ReadFormAsync(request);
                return await TextResponse("""
                    {"result":"0","loginResult":"O","motpUse":"O","motpID":"fixture-motp-id","memberID":"fixture-user","memberKey":"fixture-key","csiteNo":"0"}
                    """, "application/json");
            },
            _ => TextResponse("{\"Result\":\"\"}", "application/json"),
            async request =>
            {
                tokenForm = await ReadFormAsync(request);
                return await TextResponse("{\"tokenResult\":\"0\",\"toKen\":\"game-token\"}", "application/json");
            });

        using var client = CreateClient(handler);
        var captcha = await client.PrepareLoginAsync(CancellationToken.None);
        var login = await client.LoginAsync(
            captcha, "fixture-user", "password-sentinel", "abcde", CancellationToken.None);
        var authenticated = await client.SubmitOtpAsync(
            login.OtpChallenge!, "1234567", CancellationToken.None);
        var token = await client.CreateGameTokenAsync(
            authenticated.AuthenticatedSession!, CancellationToken.None);

        Assert.AreEqual("image/png", captcha.MediaType);
        Assert.AreEqual(KoreanLoginStatus.OtpRequired, login.Status);
        Assert.AreEqual("game-token", token);
        Assert.AreEqual("password-sentinel", loginForm!["passWord"]);
        Assert.AreEqual("abcde", loginForm["CaptchaCode"]);
        Assert.AreEqual("fixture-key", tokenForm!["memberKey"]);
        Assert.AreEqual("password-sentinel", tokenForm["passWord"]);
    }

    [TestMethod]
    public async Task InvalidCaptchaProducesTypedErrorWithoutResponseBody()
    {
        var handler = new FixtureHandler(
            _ => TextResponse(LoginHtml, "text/html"),
            _ => CaptchaResponse(),
            _ => TextResponse(
                "{\"result\":\"-106\",\"diagnostic\":\"response-secret\"}",
                "application/json"));

        using var client = CreateClient(handler);
        var captcha = await client.PrepareLoginAsync(CancellationToken.None);
        var exception = await Assert.ThrowsExactlyAsync<KoreanLauncherException>(() =>
            client.LoginAsync(captcha, "user", "password", "wrong", CancellationToken.None));

        Assert.AreEqual(KoreanLauncherError.InvalidCaptcha, exception.Error);
        Assert.AreEqual("-106", exception.ServerCode);
        Assert.IsFalse(exception.ToString().Contains("response-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmptyMotpIdIsForwardedToOtpCheck()
    {
        Dictionary<string, string>? otpForm = null;
        var handler = new FixtureHandler(
            _ => TextResponse(LoginHtml, "text/html"),
            _ => CaptchaResponse(),
            _ => TextResponse(
                """{"result":"0","loginResult":"O","motpUse":"O","motpID":"","memberID":"fixture-user","memberKey":"fixture-key","csiteNo":"0"}""",
                "application/json"),
            async request =>
            {
                otpForm = await ReadFormAsync(request);
                return await TextResponse("{\"Result\":\"\"}", "application/json");
            });

        using var client = CreateClient(handler);
        var captcha = await client.PrepareLoginAsync(CancellationToken.None);
        var login = await client.LoginAsync(
            captcha, "fixture-user", "password", "abcde", CancellationToken.None);
        var authenticated = await client.SubmitOtpAsync(
            login.OtpChallenge!, "1234567", CancellationToken.None);

        Assert.AreEqual(KoreanLoginStatus.Authenticated, authenticated.Status);
        Assert.AreEqual(string.Empty, otpForm!["motpID"]);
    }

    [TestMethod]
    public void CustomLauncherEndpointMustUseHttps()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            KoreanLauncherClient.Create(new Uri("http://127.0.0.1/")));
    }

    private static KoreanLauncherClient CreateClient(HttpMessageHandler handler)
    {
        return new KoreanLauncherClient(new HttpClient(handler)
        {
            BaseAddress = KoreanLauncherClient.LauncherBaseUri,
        });
    }

    private static Task<HttpResponseMessage> TextResponse(string body, string contentType)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });
    }

    private static Task<HttpResponseMessage> CaptchaResponse()
    {
        var content = new ByteArrayContent(new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        });
        content.Headers.ContentType = new("image/png");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> responses;

        public FixtureHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses)
        {
            this.responses = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.IsTrue(responses.Count > 0, $"Unexpected request: {request.RequestUri}");
            return responses.Dequeue()(request);
        }
    }
}
