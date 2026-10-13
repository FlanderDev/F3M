using F3M.Server.Services.Setup;
using F3M.Shared.Models;
using HtmlAgilityPack;
using System.Net;
using System.Text.Json;

namespace F3M.Server.Services;

/// <summary>
/// Handles all direct HTTP communication with F95zone (XenForo 2).
/// Registered as singleton so the login cookie jar is reused across requests. The bot account comes from
/// <see cref="F95BotSettings"/>; when it changes there, the next request signs in again with the new one.
/// </summary>
public sealed class F95Service(F95BotSettings settings, ILogger<F95Service> logger) : IDisposable
{
    private readonly ILogger<F95Service> _logger = logger;

    // Replaced when the bot account changes. Null until the first request.
    private HttpClient _httpClient = null!;
    private F95BotConfig _config = null!;
    private int _configVersion = -1;

    private string? _xfToken;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _isLoggedIn;

    /// <summary>Signs in again with the bot account in use, for the setup page's "Test login".</summary>
    public async Task<BotTestResultDto> TestLoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct);
        try
        {
            _configVersion = -1; // a fresh session, so the test really signs in
            _isLoggedIn = false;
            UseCurrentAccount();
            await LoginAsync(ct);
            return new BotTestResultDto { Success = true, At = DateTime.UtcNow, Message = $"Signed in to {_config.BaseUrl} as {_config.Username}." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The F95zone bot test login failed.");
            var message = ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message;
            return new BotTestResultDto { Success = false, At = DateTime.UtcNow, Message = message };
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Starts a new session (cookie jar) when the bot account in use changed since the last sign-in. Throws
    /// <see cref="InvalidOperationException"/> when no complete account is set up. Call under <see cref="_loginLock"/>.
    /// </summary>
    private void UseCurrentAccount()
    {
        var version = settings.Version;
        if (version == _configVersion)
            return;

        var (config, _) = settings.Current();
        if (!config.IsComplete)
            throw new InvalidOperationException("No F95zone bot account is set up. An admin can add one on the setup page.");

        // A manually created handler with a persistent CookieContainer is required —
        // IHttpClientFactory rotates handlers and discards cookies between requests.
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            UseCookies = true
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/")
        };

        client.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        _httpClient?.Dispose();
        _httpClient = client;
        _config = config;
        _configVersion = version;
        _isLoggedIn = false;
        _xfToken = null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the target user's profile page and returns every post and comment on it, with its author, plus what
    /// the caller needs to explain a miss. The caller decides which posts count and looks for the code in them.
    /// </summary>
    public async Task<F95ProfilePage> GetProfilePageAsync(
        string targetUsername,
        string targetUserId,
        CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        var relativeUrl = $"members/{targetUsername}.{targetUserId}/";
        var (html, finalUri) = await FetchWithReloginAsync(relativeUrl, ct);

        // A session F95zone has dropped can still get the page, just as a guest, who may not see the posts.
        if (!IsSignedIn(html))
        {
            _logger.LogWarning("The profile page of {Username} came back signed out; signing in again.", targetUsername);
            await ForceReloginAsync(ct);
            (html, finalUri) = await FetchWithReloginAsync(relativeUrl, ct);
        }

        EnsureProfileIs(html, finalUri, targetUsername, targetUserId);
        return ParseProfilePage(html, finalUri);
    }

    private async Task ForceReloginAsync(CancellationToken ct)
    {
        _isLoggedIn = false;
        await EnsureLoggedInAsync(ct);
    }

    /// <summary>XenForo marks pages for signed-in visitors with &lt;html data-logged-in="true"&gt;.</summary>
    private static bool IsSignedIn(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectSingleNode("//html")?.GetAttributeValue("data-logged-in", null) == "true";
    }

    /// <summary>
    /// F95zone finds a profile by its number alone and redirects to its real address. A URL that pairs someone's
    /// number with another name would otherwise show that someone's profile, where posts by the named user (left on
    /// their wall) would pass as proof: the account of the profile's owner would be taken over. So the page must be
    /// the profile of exactly this name and number, by the address it ended on and by its canonical link.
    /// </summary>
    /// <exception cref="ProfileMismatchException">The page belongs to another name or number.</exception>
    private static void EnsureProfileIs(string html, Uri? finalUri, string username, string userId)
    {
        var addresses = new List<string>();
        if (finalUri is not null)
            addresses.Add(finalUri.AbsoluteUri);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        if (doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", null) is { Length: > 0 } canonical)
            addresses.Add(WebUtility.HtmlDecode(canonical));

        foreach (var address in addresses)
        {
            if (F3M.Shared.Helpers.F95Profile.TryParse(address, out var name, out var id)
                && (!string.Equals(name, username, StringComparison.OrdinalIgnoreCase) || id != userId))
            {
                throw new ProfileMismatchException(name, id);
            }
        }
    }

    // ── Login & Session ───────────────────────────────────────────────────────

    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        if (_isLoggedIn && settings.Version == _configVersion) return;

        await _loginLock.WaitAsync(ct);
        try
        {
            UseCurrentAccount();
            if (_isLoggedIn) return;
            await LoginAsync(ct);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        _logger.LogInformation("Logging into F95zone as {Username}.", _config.Username);

        // GET login page to obtain the CSRF token.
        var loginPageHtml = await _httpClient.GetStringAsync("login/", ct);
        _xfToken = ExtractXfToken(loginPageHtml)
            ?? throw new InvalidOperationException(
                "Could not extract _xfToken from the F95zone login page.");

        var formData = new Dictionary<string, string>
        {
            ["login"] = _config.Username,
            ["password"] = _config.Password,
            ["_xfToken"] = _xfToken,
            ["remember"] = "1",
            ["_xfResponseType"] = "json"
        };

        var response = await _httpClient.PostAsync(
            "login/login", new FormUrlEncodedContent(formData), ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"F95zone login failed: the site answered with HTTP {(int)response.StatusCode} instead of a sign-in result.");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("status", out var statusEl) ||
                statusEl.GetString() != "ok")
            {
                throw new InvalidOperationException(
                    $"F95zone login failed. Response: {body}");
            }
        }

        // Refresh the CSRF token from the home page after a successful login.
        var homeHtml = await _httpClient.GetStringAsync("", ct);
        _xfToken = ExtractXfToken(homeHtml) ?? _xfToken;

        _isLoggedIn = true;
        _logger.LogInformation("Successfully logged into F95zone.");
    }

    #region Helper

    // ── Profile Wall Parsing ──────────────────────────────────────────────────

    /// <summary>
    /// Reads every profile post and comment from a rendered member profile page, with its author's user id and name.
    ///
    /// F95zone's markup (checked against a live profile page, 2026-10):
    ///   &lt;article class="message ..." id="js-profilePost-{id}" data-author="{name}"&gt;
    ///     ... &lt;a class="avatar" data-user-id="{author id}"&gt; ... &lt;article class="message-body"&gt;text&lt;/article&gt; ...
    ///   &lt;/article&gt;
    ///   &lt;div class="comment" id="js-profilePostComment-{id}" data-author="{name}"&gt;
    ///     ... &lt;a class="avatar" data-user-id="{author id}"&gt; ... &lt;article class="comment-body"&gt;text&lt;/article&gt; ...
    ///   &lt;/div&gt;
    /// Matched by id on any element and by class words, so a change of tag doesn't break it. XenForo's usual
    /// &lt;div class="bbWrapper"&gt; is taken first in case F95zone adds it back.
    /// </summary>
    private static F95ProfilePage ParseProfilePage(string html, Uri? finalUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var posts = new List<F95ProfilePost>();
        var nodes = doc.DocumentNode.SelectNodes("//*[starts-with(@id, 'js-profilePost-') or starts-with(@id, 'js-profilePostComment-')]");
        foreach (var node in nodes ?? Enumerable.Empty<HtmlNode>())
        {
            var id = node.GetAttributeValue("id", string.Empty);
            var isComment = id.StartsWith("js-profilePostComment-", StringComparison.Ordinal);
            if (!long.TryParse(id[(isComment ? "js-profilePostComment-" : "js-profilePost-").Length..], out var postId))
                continue;

            // The first user link inside is the author's avatar; reactions and the like come later.
            var authorId = node.SelectSingleNode(".//*[@data-user-id]")?.GetAttributeValue("data-user-id", null);
            var bodyNode = node.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' bbWrapper ')]")
                           ?? node.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' message-body ')]")
                           ?? node.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' comment-body ')]");

            posts.Add(new F95ProfilePost(
                postId,
                isComment,
                authorId,
                WebUtility.HtmlDecode(node.GetAttributeValue("data-author", string.Empty)),
                WebUtility.HtmlDecode(bodyNode?.InnerText ?? string.Empty).Trim()));
        }

        var title = WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty).Trim();
        var pageText = WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//body")?.InnerText ?? string.Empty);
        return new F95ProfilePage(finalUri, title, IsSignedIn(html), html.Length, posts, pageText);
    }
    // ── Token / Session Helpers ───────────────────────────────────────────────

    private async Task RefreshXfTokenAsync(CancellationToken ct)
    {
        var html = await _httpClient.GetStringAsync("", ct);
        var token = ExtractXfToken(html);
        if (token is not null) _xfToken = token;
    }

    private static string? ExtractXfToken(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // XenForo 2 embeds the CSRF token in <html data-csrf="TOKEN">.
        var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
        var token = htmlNode?.GetAttributeValue("data-csrf", null);
        if (token is not null) return token;

        // Fallback: hidden input in forms.
        var input = doc.DocumentNode.SelectSingleNode("//input[@name='_xfToken']");
        return input?.GetAttributeValue("value", null);
    }

    /// <summary>The page's HTML and the address it was served from, after redirects.</summary>
    private async Task<(string Html, Uri? FinalUri)> FetchWithReloginAsync(string relativeUrl, CancellationToken ct)
    {
        var response = await _httpClient.SendAsync(FreshRequest(relativeUrl), ct);

        if (IsRedirectToLogin(response) || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Session expired — re-logging in.");
            await ForceReloginAsync(ct);
            response = await _httpClient.SendAsync(FreshRequest(relativeUrl), ct);
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(ct), response.RequestMessage?.RequestUri);
    }

    /// <summary>A GET that asks every cache on the way for the current page: a post made a moment ago must be on it.</summary>
    private static HttpRequestMessage FreshRequest(string relativeUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        return request;
    }

    private static bool IsRedirectToLogin(HttpResponseMessage response)
        => response.RequestMessage?.RequestUri?.AbsolutePath
               .Contains("/login", StringComparison.OrdinalIgnoreCase) == true;

    public void Dispose()
    {
        _httpClient?.Dispose();
        _loginLock.Dispose();
    }

    #endregion
}

/// <summary>The profile page F95zone showed belongs to another name or number than the URL that was entered.</summary>
public sealed class ProfileMismatchException(string actualName, string actualId)
    : Exception($"The profile address leads to {actualName}.{actualId}.")
{
    public string ActualName { get; } = actualName;
    public string ActualId { get; } = actualId;
}

/// <summary>A profile post, or a comment on one (<see cref="IsComment"/>), as read from a member profile page.</summary>
public sealed record F95ProfilePost(long Id, bool IsComment, string? AuthorId, string AuthorName, string Text);

/// <summary>What the bot saw on a member profile page: the posts, and enough about the page to explain a miss.</summary>
public sealed record F95ProfilePage(Uri? FinalUri, string Title, bool SignedIn, int HtmlLength, List<F95ProfilePost> Posts, string PageText);
