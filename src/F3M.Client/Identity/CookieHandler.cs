
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Net;

namespace F3M.Client.Identity;

/// <summary>
/// Handler to ensure cookie credentials are automatically sent over with each request.
/// </summary>
public class CookieHandler(AuthStateSignal authState) : DelegatingHandler
{
    /// <summary>
    /// Main method to override for the handler.
    /// </summary>
    /// <param name="request">The original request.</param>
    /// <param name="cancellationToken">The token to handle cancellations.</param>
    /// <returns>The <see cref="HttpResponseMessage"/>.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // include cookies!
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.Headers.Add("X-Requested-With", ["XMLHttpRequest"]);

        var response = await base.SendAsync(request, cancellationToken);

        // If Refused, then roles might be stale. Only for /api: account-endpoints serve 401 for every guest.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            && request.RequestUri?.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true)
            authState.RaiseRefused();

        return response;
    }
}
