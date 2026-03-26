using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// A <see cref="DelegatingHandler"/> that lazily resolves the authentication header
/// on each outgoing request, ensuring that configuration (options, tokens) is not
/// accessed until the first actual API call — after MCP initialization.
/// <para>
/// When the server returns HTTP 203 with an HTML body (Azure DevOps authentication redirect
/// served by a CDN), the handler invalidates the cached token, acquires a fresh one via
/// Azure CLI, and retries the request once before returning the response.
/// </para>
/// </summary>
public class AuthenticationDelegatingHandler : DelegatingHandler
{
    private readonly IAuthenticationProvider _authenticationProvider;
    private readonly ILogger<AuthenticationDelegatingHandler> _logger;

    public AuthenticationDelegatingHandler(
        IAuthenticationProvider authenticationProvider,
        ILogger<AuthenticationDelegatingHandler> logger)
    {
        _authenticationProvider = authenticationProvider;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = await _authenticationProvider.GetAuthenticationAsync(cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);

        if (IsAuthFailureResponse(response))
        {
            _logger.LogWarning(
                "Received HTTP {StatusCode} — token appears expired. Invalidating cache and retrying with a fresh token.",
                (int)response.StatusCode);

            _authenticationProvider.InvalidateCachedToken();

            // Clone the request before retrying — HttpRequestMessage can only be sent once
            var retryRequest = await CloneRequestAsync(request, cancellationToken);
            retryRequest.Headers.Authorization = await _authenticationProvider.GetAuthenticationAsync(cancellationToken);

            response.Dispose();
            response = await base.SendAsync(retryRequest, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// Detects an authentication failure disguised as a 2xx response with an HTML body
    /// (Azure DevOps CDN returns HTTP 203 with a login redirect page when the token is invalid).
    /// </summary>
    private static bool IsAuthFailureResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return false;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        return string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage original,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (original.Content is not null)
        {
            var content = await original.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(content);

            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
