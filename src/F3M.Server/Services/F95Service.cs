using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// Posts a verification challenge on the bot's own profile wall mentioning the target user.
    /// Returns the profile post ID needed to later check for replies.
    /// </summary>
    public async Task<long> PostVerificationChallengeAsync(
        string targetUsername,
        string targetUserId,
        string guid,
        CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        var message =
            string.Join('\n', [
                $"User: '{targetUsername}'",
                $"Id: '{targetUserId}'",
                "Your F3M account verification code is:",
                guid,
                "\n",
                "Please reply to this post with your verification code to complete linking your F95zone account to F3M.",
                "This code expires in 24 hours."
                ]);

        return await PostOnOwnProfileWallAsync(message, ct);
    }

    /// <summary>
    /// Fetches all comments on a specific profile post and returns the text
    /// of those whose author matches <paramref name="targetF95UserId"/>.
    /// </summary>
    public async Task<List<string>> GetCommentsFromUserAsync(
        long profilePostId,
        string targetF95UserId,
        CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        var html = await FetchWithReloginAsync($"profile-posts/{profilePostId}/", ct);
        return ParseCommentsFromUser(html, targetF95UserId);
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

    // ── Profile Wall ──────────────────────────────────────────────────────────

    private async Task<long> PostOnOwnProfileWallAsync(string message, CancellationToken ct)
    {
        await RefreshXfTokenAsync(ct);

        var url = $"members/{_config.UserId}/post";

        var formData = new Dictionary<string, string>
        {
            ["message"] = message,
            ["_xfToken"] = _xfToken!,
            ["_xfResponseType"] = "json"
        };

        var response = await _httpClient.PostAsync(
            url, new FormUrlEncodedContent(formData), ct);

        if (response.StatusCode == HttpStatusCode.Forbidden || IsRedirectToLogin(response))
        {
            _isLoggedIn = false;
            await EnsureLoggedInAsync(ct);
            return await PostOnOwnProfileWallAsync(message, ct);
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);

        // Expected: { "status": "ok", "profilePost": { "profilePostId": 12345 } }
        // NOTE: If F95zone returns a different shape, update the property path here.
        using var jsonDoc = JsonDocument.Parse(body);

        if (!jsonDoc.RootElement.TryGetProperty("redirect", out var postUrl))
        {
            throw new InvalidOperationException(
                $"Unexpected profile-post response shape. Body: {body}");
        }

        var postId = long.Parse(postUrl.GetString()?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? throw new InvalidOperationException($"Could not extract profile post ID from redirect URL: {postUrl}"));
        _logger.LogInformation("Created profile post {PostId} on bot wall.", postId);
        return postId;
    }

    #region Helper

    /// <summary>
    /// Deletes a profile post by ID using XenForo's delete endpoint.
    /// The bot account must be the author of the post (or have moderation rights).
    /// </summary>
    public async Task DeleteProfilePostAsync(long profilePostId, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        await RefreshXfTokenAsync(ct);

        var url = $"profile-posts/{profilePostId}/delete";

        var formData = new Dictionary<string, string>
        {
            ["_xfToken"] = _xfToken!,
            ["_xfResponseType"] = "json"
        };

        var response = await _httpClient.PostAsync(
            url, new FormUrlEncodedContent(formData), ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            IsRedirectToLogin(response))
        {
            _isLoggedIn = false;
            await EnsureLoggedInAsync(ct);
            await DeleteProfilePostAsync(profilePostId, ct);
            return;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("status", out var status) &&
            status.GetString() == "ok")
        {
            _logger.LogInformation("Deleted profile post {PostId}.", profilePostId);
            return;
        }

        var error = doc.RootElement.TryGetProperty("errors", out var errors)
            ? errors.ToString()
            : body;

        throw new InvalidOperationException(
            $"F95zone returned an error when deleting post {profilePostId}: {error}");
    }



    // ── Comment Parsing ───────────────────────────────────────────────────────

    private List<string> ParseCommentsFromUser(string html, string targetUserId)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var commentNodes = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'js-profilePostComment-')]");

        if (commentNodes is null)
        {
            _logger.LogWarning("No comment nodes found on profile post page.");
            return [];
        }

        var results = new List<string>();

        foreach (var node in commentNodes)
        {
            // Author is on the <a class="comment-user"> element via data-user-id.
            var authorNode = node.SelectSingleNode(
                ".//*[contains(@class,'comment-user') and @data-user-id]");

            var authorId = authorNode?.GetAttributeValue("data-user-id", null);

            if (authorId != targetUserId) continue;

            // Content is inside <article class="comment-body">.
            var bodyNode = node.SelectSingleNode(
                ".//article[contains(@class,'comment-body')]");

            var text = bodyNode?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(text))
                results.Add(text);
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
