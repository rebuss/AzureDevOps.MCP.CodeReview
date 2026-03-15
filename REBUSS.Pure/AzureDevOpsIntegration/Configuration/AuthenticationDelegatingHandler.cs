namespace REBUSS.Pure.AzureDevOpsIntegration.Configuration;

/// <summary>
/// A <see cref="DelegatingHandler"/> that lazily resolves the authentication header
/// on each outgoing request, ensuring that configuration (options, tokens) is not
/// accessed until the first actual API call — after MCP initialization.
/// </summary>
public class AuthenticationDelegatingHandler : DelegatingHandler
{
    private readonly IAuthenticationProvider _authenticationProvider;

    public AuthenticationDelegatingHandler(IAuthenticationProvider authenticationProvider)
    {
        _authenticationProvider = authenticationProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = await _authenticationProvider.GetAuthenticationAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
