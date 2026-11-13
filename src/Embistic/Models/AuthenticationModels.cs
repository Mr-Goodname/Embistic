using System.Net;

namespace Embistic.Models;

public sealed record EmbyLoginResult(string AccessToken, string UserId, string UserName, string ServerId);

public sealed class EmbyAuthenticationException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public EmbyAuthenticationException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }
}
