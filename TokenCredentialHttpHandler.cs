using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

// DelegatingHandler that uses an Azure TokenCredential to attach Bearer tokens
// to outgoing HTTP requests.
internal class TokenCredentialHttpHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;

    public TokenCredentialHttpHandler(TokenCredential credential, string[] scopes)
    {
        _credential = credential;
        _scopes = scopes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
