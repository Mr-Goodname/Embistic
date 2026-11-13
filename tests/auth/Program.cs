using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embistic.Models;
using Embistic.Services;

if (args.Contains("--live"))
{
    var liveServer = Environment.GetEnvironmentVariable("EMBISTIC_AUTH_SERVER") ?? "";
    var liveUsername = Environment.GetEnvironmentVariable("EMBISTIC_AUTH_USERNAME") ?? "";
    var livePassword = Environment.GetEnvironmentVariable("EMBISTIC_AUTH_PASSWORD") ?? "";
    try
    {
        var result = await EmbyAuthenticationService.AuthenticateAsync(liveServer, liveUsername, livePassword);
        Console.WriteLine("PASS: Real Emby account login returns a user and access token.");
        if (result.AccessToken.Length == 0 || result.UserId.Length == 0) throw new InvalidOperationException("Missing login result.");
    }
    catch (EmbyAuthenticationException exception)
    {
        Console.WriteLine("FAIL: Real account login: " + exception.Message + " HTTP " + exception.StatusCode);
        Environment.ExitCode = 1;
    }
    return;
}

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

var tcp = new TcpListener(IPAddress.Loopback, 0);
tcp.Start();
var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
tcp.Stop();
var url = $"http://127.0.0.1:{port}";
using var listener = new HttpListener();
listener.Prefixes.Add(url + "/");
listener.Start();

async Task<EmbyLoginResult> WithResponse(int statusCode, string response, string password = " correct password ")
{
    var server = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        var request = context.Request;
        Check(request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/Users/AuthenticateByName", "Standard Emby account login endpoint");
        Check(request.Headers["X-Emby-Token"] is null && request.Url?.Query == "", "No credential in URL or token header");
        Check(request.ContentLength64 > 0 && request.Headers["Transfer-Encoding"] is null,
            "Login JSON has an explicit length for Emby servers that reject chunked requests");
        var expectedId = "embistic-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName)))[..24].ToLowerInvariant();
        var auth = request.Headers["X-Emby-Authorization"] ?? "";
        var agentVersion = request.UserAgent?.Split('/').LastOrDefault();
        Check(auth.Contains("Client=\"Embistic\"") && auth.Contains($"DeviceId=\"{expectedId}\"")
            && Version.TryParse(agentVersion, out _) && auth.Contains($"Version=\"{agentVersion}\""),
            "Stable Windows client identity");
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
        Check(body.RootElement.GetProperty("Username").GetString() == "Listener" && body.RootElement.GetProperty("Pw").GetString() == password,
            "Username is trimmed and password spaces preserved");
        Check(body.RootElement.EnumerateObject().Count() == 2, "Only required login credentials are sent");
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response));
        context.Response.Close();
    });
    try { return await EmbyAuthenticationService.AuthenticateAsync(url, " Listener ", password); }
    finally { await server; }
}

var login = await WithResponse(200, """{"AccessToken":"fixture-access-token","User":{"Id":"u1","Name":"Listener"},"ServerId":"server1"}""");
Check(login is { AccessToken: "fixture-access-token", UserId: "u1", UserName: "Listener", ServerId: "server1" }, "Login token and user parsed");

login = await WithResponse(200, """{"AccessToken":"fixture-access-token","User":{"Id":"u1"}}""", "");
Check(login.UserName == "Listener", "Passwordless accounts and missing display name accepted");

async Task ExpectSanitizedFailure(int status, string response, string description)
{
    try { await WithResponse(status, response); throw new InvalidOperationException("Expected auth failure"); }
    catch (EmbyAuthenticationException ex)
    {
        Check(!ex.ToString().Contains("private-secret") && !ex.ToString().Contains("correct password") && !ex.ToString().Contains(url),
            description);
    }
}
await ExpectSanitizedFailure(401, "private-secret correct password http://example.invalid/?token=private-secret", "401 failure excludes response body and credentials");
await ExpectSanitizedFailure(403, """{"Error":"private-secret"}""", "403 permission error is sanitized");
await ExpectSanitizedFailure(500, "private-secret", "Server failure is sanitized");
await ExpectSanitizedFailure(302, "private-secret", "Redirect is rejected without forwarding the password");
await ExpectSanitizedFailure(200, """{"AccessToken":"private-secret","User":null}""", "Malformed login object cannot expose token");
await ExpectSanitizedFailure(200, "private-secret", "Non-JSON body is sanitized");

foreach (var invalid in new[] { "file:///tmp/emby", "https://user:private-secret@example.invalid", "http://example.invalid/?token=private-secret", "http://example.invalid/#secret", "" })
{
    try { await EmbyAuthenticationService.AuthenticateAsync(invalid, "Listener", "password"); throw new InvalidOperationException("Expected invalid URL"); }
    catch (ArgumentException ex) { Check(!ex.Message.Contains("private-secret"), "Invalid URL rejected without echoing supplied values"); }
}
try { await EmbyAuthenticationService.AuthenticateAsync(url, " ", "password"); throw new InvalidOperationException("Expected empty username"); }
catch (ArgumentException) { Check(true, "Empty username rejected before network request"); }

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await EmbyAuthenticationService.AuthenticateAsync(url, "Listener", "password", cancelled.Token); throw new InvalidOperationException("Expected cancellation"); }
catch (OperationCanceledException) { Check(true, "Caller cancellation is preserved"); }

var slowBody = Task.Run(async () =>
{
    var context = await listener.GetContextAsync();
    context.Response.ContentType = "application/json";
    context.Response.SendChunked = true;
    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"AccessToken":"""));
    await context.Response.OutputStream.FlushAsync();
    await Task.Delay(300);
    context.Response.Close();
});
using var bodyCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
try { await EmbyAuthenticationService.AuthenticateAsync(url, "Listener", "password", bodyCancellation.Token); throw new InvalidOperationException("Expected response-body cancellation"); }
catch (OperationCanceledException) { Check(true, "Caller cancellation also interrupts a slow response body"); }
await slowBody;

Console.WriteLine($"Completed: {checks} account-login checks passed.");
