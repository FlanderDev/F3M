using System.Net;
using System.Text.Json;
using HtmlAgilityPack;

namespace F3M.Server.Services;

/// <summary>
/// Handles all direct HTTP communication with F95zone (XenForo 2).
/// Registered as singleton so the login cookie jar is reused across requests.
/// </summary>
public sealed class F95Service : IDisposable
{
    private sealed class F95BotConfig
    {
        public required string BaseUrl { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }

        /// <summary>Numeric user ID of the bot account (visible in its profile URL).</summary>
        public required string UserId { get; init; }
    }


    private readonly HttpClient _httpClient;
    private readonly ILogger<F95Service> _logger;
    private readonly F95BotConfig _config;

    private string? _xfToken;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _isLoggedIn;

    public F95Service(IConfiguration configuration, ILogger<F95Service> logger)
    {
        _logger = logger;
        _config = configuration.GetSection("F95Bot").Get<F95BotConfig>()
            ?? throw new InvalidOperationException("F95Bot configuration section is missing.");

        // A manually created handler with a persistent CookieContainer is required —
        // IHttpClientFactory rotates handlers and discards cookies between requests.
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true,
            UseCookies = true
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/') + "/")
        };

        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the target user's profile wall and returns the (PostId, Text) of every
    /// top-level profile post authored by them. The caller checks these for the verification GUID.
    /// </summary>
    public async Task<List<(long PostId, string Text)>> GetProfilePostsAsync(
        string targetUsername,
        string targetUserId,
        CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        var html = await FetchWithReloginAsync($"members/{targetUsername}.{targetUserId}/", ct);
        return ParseProfilePosts(html, targetUsername);
    }

    // ── Login & Session ───────────────────────────────────────────────────────

    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        if (_isLoggedIn) return;

        await _loginLock.WaitAsync(ct);
        try
        {
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

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("status", out var statusEl) ||
            statusEl.GetString() != "ok")
        {
            throw new InvalidOperationException(
                $"F95zone login failed. Response: {body}");
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
    /// Parses top-level profile posts (the user's profile posts, excluding
    /// comments on that post) from a rendered member profile page, filtered to those authored by
    /// <paramref name="targetUsername"/>.
    ///
    /// NOTE: This assumes XenForo 2's default markup:
    ///   &lt;div class="message ... js-profilePost-{id}" data-author="{username}"&gt;
    ///     &lt;article class="message-body"&gt;
    ///       &lt;div class="bbWrapper"&gt;...text...&lt;/div&gt;
    ///     &lt;/article&gt;
    ///   &lt;/div&gt;
    /// This has NOT been verified against a live page capture yet — if matching keeps coming
    /// back empty, inspect an actual profile page's HTML and adjust the selectors below.
    /// </summary>
    private List<(long PostId, string Text)> ParseProfilePosts(string html, string targetUsername)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Top-level posts use id="js-profilePost-{id}"; comments use the
        // "js-profilePostComment-{id}" prefix, so explicitly exclude those.
        var postNodes = doc.DocumentNode.SelectNodes(
            "//div[starts-with(@id, 'js-profilePost-') and not(starts-with(@id, 'js-profilePostComment-'))]");

        if (postNodes is null)
        {
            _logger.LogWarning("No profile post nodes found on member profile page for {Username}.", targetUsername);
            return [];
        }

        var results = new List<(long, string)>();

        foreach (var node in postNodes)
        {
            var author = node.GetAttributeValue("data-author", null);
            if (!string.Equals(author, targetUsername, StringComparison.OrdinalIgnoreCase))
                continue;

            var idAttr = node.GetAttributeValue("id", string.Empty);
            var idPart = idAttr.Replace("js-profilePost-", string.Empty);
            if (!long.TryParse(idPart, out var postId))
                continue;

            // Content is inside <div class="bbWrapper"> within the message body.
            var bodyNode = node.SelectSingleNode(".//div[contains(@class,'bbWrapper')]");

            var text = bodyNode?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(text))
                results.Add((postId, text));
        }

        return results;
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

    private async Task<string> FetchWithReloginAsync(string relativeUrl, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(relativeUrl, ct);

        if (IsRedirectToLogin(response) || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Session expired — re-logging in.");
            _isLoggedIn = false;
            await EnsureLoggedInAsync(ct);
            response = await _httpClient.GetAsync(relativeUrl, ct);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static bool IsRedirectToLogin(HttpResponseMessage response)
        => response.RequestMessage?.RequestUri?.AbsolutePath
               .Contains("/login", StringComparison.OrdinalIgnoreCase) == true;

    public void Dispose()
    {
        _httpClient.Dispose();
        _loginLock.Dispose();
    }

    #endregion
}
