using System.Net;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Signs in with an Emby account and returns a revocable access token. Passwords are never retained.</summary>
public static class EmbyAuthenticationService
{
    private static readonly string DeviceId = "embistic-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName)))[..24].ToLowerInvariant();
    private static readonly JsonSerializerOptions CredentialsJson = new() { PropertyNamingPolicy = null };

    public static async Task<EmbyLoginResult> AuthenticateAsync(
        string serverUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var uri = ValidateServerUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("请输入 Emby 用户名。", nameof(username));
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false
        })
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Authorization",
            $"MediaBrowser Client=\"Embistic\", Device=\"Windows\", DeviceId=\"{DeviceId}\", Version=\"0.4.0\"");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Embistic/0.4.0");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName")
            {
                // Keep the password exact, including spaces; empty passwords are valid on passwordless Emby accounts.
                // Some Emby servers reject chunked authentication requests. Buffer this
                // small JSON body so HttpClient sends an explicit Content-Length.
                Content = new StringContent(JsonSerializer.Serialize(new { Username = username.Trim(), Pw = password ?? "" }, CredentialsJson),
                    Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new EmbyAuthenticationException(ErrorFor(response.StatusCode), response.StatusCode);

            // The same overall deadline also bounds the response body after headers arrive.
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("User", out var user)
                || user.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();

            var token = String(root, "AccessToken");
            var userId = String(user, "Id");
            var userName = String(user, "Name");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userId))
                throw InvalidResponse();

            return new EmbyLoginResult(token, userId, string.IsNullOrWhiteSpace(userName) ? username.Trim() : userName,
                String(root, "ServerId"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbyAuthenticationException("登录请求超时，请检查服务器地址和网络后重试。");
        }
        catch (HttpRequestException)
        {
            // Server response bodies and network exception details can contain credentials or private URLs.
            throw new EmbyAuthenticationException("无法连接 Emby，请检查服务器地址和网络。");
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (IOException)
        {
            throw new EmbyAuthenticationException("登录响应读取失败，请检查网络后重试。");
        }
    }

    private static Uri ValidateServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate((serverUrl ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入有效的 Emby 地址，例如 http://192.168.1.10:8096。", nameof(serverUrl));
        return uri;
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static EmbyAuthenticationException InvalidResponse() =>
        new("服务器返回了无效的登录信息，请确认地址指向 Emby。");

    private static string ErrorFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "登录失败，请检查用户名、密码和账户权限。",
        HttpStatusCode.TooManyRequests =>
            "登录请求过于频繁，请稍后重试。",
        HttpStatusCode.NotFound =>
            "未找到 Emby 登录接口，请检查服务器地址。",
        >= HttpStatusCode.InternalServerError =>
            "Emby 暂时无法处理登录，请稍后重试。",
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
            "登录地址发生重定向，请输入重定向后的 Emby 地址。",
        _ => "服务器拒绝了登录请求，请检查地址和账号。"
    };
}
