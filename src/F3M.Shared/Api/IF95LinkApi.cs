using F3M.Shared;
using F3M.Shared.Models;
using FlanderDev.RouteGen;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for F95zone account linking/login. Deliberately has no [Authorize] anywhere —
/// this IS the login/registration mechanism, so nothing here can require authentication.
///
/// Both operations always return 200 OK; the outcome (pending/verified/expired/error/not-found)
/// is carried entirely by the response body's Status field, never by the HTTP status code. This
/// mirrors ChangePasswordAsync's same choice on IProfileApi: these are expected, "normal"
/// outcomes of a polling flow, not exceptional conditions that should throw ApiException on the
/// client — LinkAccount.razor.cs switches on result.Status directly and never inspected the raw
/// HTTP status code even before this migration, so this is a pure simplification, not a
/// client-visible behavior change.
/// </summary>
[ApiRoute("api/auth/f95", HttpClientName = Configuration.AppName)]
public interface IF95LinkApi
{
    [Post("start")]
    Task<LinkF95StartResponse> Start([Body] LinkF95StartRequest request, CancellationToken ct = default);

    [Post("check/{f95UserId}")]
    Task<LinkF95PollResponse> Check(string f95UserId, [Body] LinkF95PollRequest request, CancellationToken ct = default);
}
