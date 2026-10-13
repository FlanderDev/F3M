using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

namespace F3M.Server.Services.Setup;

/// <summary>The F95zone bot account <see cref="F95Service"/> signs in with.</summary>
public sealed record F95BotConfig(string BaseUrl, string Username, string Password, string UserId)
{
    public bool IsComplete => Username.Length > 0 && Password.Length > 0 && Uri.TryCreate(BaseUrl, UriKind.Absolute, out _);
}

/// <summary>
/// Which F95zone bot account is in use: the one saved on the setup page, or else the "F95Bot" settings
/// (F95Bot__* environment variables). The saved one lives in Storage/Secrets/f95-bot.json, its password encrypted with
/// the site's data-protection keys (Storage/Secrets/DataProtection).
/// </summary>
public sealed class F95BotSettings(IConfiguration configuration, IDataProtectionProvider protection, ILogger<F95BotSettings> logger)
{
    public const string SectionName = "F95Bot";
    private const string DefaultBaseUrl = "https://f95zone.to";

    private static string FilePath => Path.Combine(Assets.StorageRoot, "Secrets", "f95-bot.json");

    private readonly IDataProtector protector = protection.CreateProtector("F3M.F95Bot");
    private readonly Lock gate = new();
    private SavedBot? saved;
    private bool loaded;
    private int version;
    private BotTestResultDto? lastTest;

    /// <summary>Changes whenever the account in use may have changed, so <see cref="F95Service"/> signs in again.</summary>
    public int Version
    {
        get
        {
            lock (gate)
                return version;
        }
    }

    public (F95BotConfig Config, BotSettingsSource Source) Current()
    {
        lock (gate)
        {
            if (Saved() is { } site)
                return (site.ToConfig(protector), BotSettingsSource.Site);
        }

        var environment = Environment();
        return (environment, environment.IsComplete ? BotSettingsSource.Environment : BotSettingsSource.None);
    }

    public bool IsConfigured => Current().Source != BotSettingsSource.None;

    public BotTestResultDto? LastTest
    {
        get
        {
            lock (gate)
                return lastTest;
        }
        set
        {
            lock (gate)
                lastTest = value;
        }
    }

    public BotSettingsDto Describe()
    {
        var (config, source) = Current();
        return new BotSettingsDto
        {
            Source = source,
            BaseUrl = source == BotSettingsSource.None && config.BaseUrl.Length == 0 ? DefaultBaseUrl : config.BaseUrl,
            Username = config.Username,
            UserId = config.UserId,
            HasPassword = config.Password.Length > 0,
            EnvironmentConfigured = Environment().IsComplete,
            LastTest = LastTest,
        };
    }

    /// <summary>Saves the account on the site. Throws <see cref="InvalidOperationException"/> for invalid input.</summary>
    public void Save(BotSettingsUpdateDto dto)
    {
        var baseUrl = dto.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
            baseUrl = DefaultBaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The F95zone address must be an https:// address, e.g. https://f95zone.to.");

        var username = dto.Username.Trim();
        if (username.Length == 0)
            throw new InvalidOperationException("Enter the bot account's user name.");

        var userId = dto.UserId.Trim();
        if (userId.Length > 0 && !userId.All(char.IsAsciiDigit))
            throw new InvalidOperationException("The user ID is the number in the bot's profile address, e.g. 12345.");

        lock (gate)
        {
            var password = dto.Password is { Length: > 0 } entered
                ? protector.Protect(entered)
                : Saved()?.ProtectedPassword;
            if (password is null)
                throw new InvalidOperationException("Enter the bot account's password.");

            var next = new SavedBot(baseUrl, username, userId, password);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, FilePath, overwrite: true);

            saved = next;
            Changed();
        }

        logger.LogInformation("The F95zone bot account was saved on the setup page ({Username}).", username);
    }

    /// <summary>Removes the saved account; the "F95Bot" settings apply again.</summary>
    public void Remove()
    {
        lock (gate)
        {
            File.Delete(FilePath);
            saved = null;
            loaded = true;
            Changed();
        }

        logger.LogInformation("The F95zone bot account saved on the setup page was removed.");
    }

    private void Changed()
    {
        version++;
        lastTest = null;
    }

    private F95BotConfig Environment()
    {
        var section = configuration.GetSection(SectionName);
        return new F95BotConfig(
            (section["BaseUrl"] ?? string.Empty).Trim(),
            (section["Username"] ?? string.Empty).Trim(),
            section["Password"] ?? string.Empty,
            (section["UserId"] ?? string.Empty).Trim());
    }

    /// <summary>The saved account, read once. Call under <see cref="gate"/>.</summary>
    private SavedBot? Saved()
    {
        if (loaded)
            return saved;

        loaded = true;
        if (!File.Exists(FilePath))
            return saved = null;

        try
        {
            saved = JsonSerializer.Deserialize<SavedBot>(File.ReadAllText(FilePath));
            _ = saved?.ToConfig(protector); // fails here when the data-protection keys are gone
        }
        catch (Exception ex) when (ex is JsonException or IOException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogError(ex, "The saved F95zone bot account in {Path} can't be read; the F95Bot settings apply. Save it again on the setup page.", FilePath);
            saved = null;
        }

        return saved;
    }

    private sealed record SavedBot(string BaseUrl, string Username, string UserId, string ProtectedPassword)
    {
        public F95BotConfig ToConfig(IDataProtector protector) =>
            new(BaseUrl, Username, protector.Unprotect(ProtectedPassword), UserId);
    }
}
