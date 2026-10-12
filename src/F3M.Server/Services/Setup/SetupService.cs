using F3M.Server.Data;
using F3M.Server.Services.Import;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services.Setup;

/// <summary>
/// Server side of the setup page (<see cref="ISetupApi"/>): a checklist of what a new site still needs, and the F95zone
/// bot account. The frameworks are imported through <see cref="IImportApi.StartFrameworksAsync"/>.
/// </summary>
public sealed class SetupService(
    AppDbContext db,
    AdminAccount adminAccount,
    F95BotSettings botSettings,
    F95Service f95,
    CatalogService catalog,
    F95ImportService import,
    ILogger<SetupService> logger) : ISetupApi
{
    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var bot = botSettings.Describe();
        var frameworks = await FrameworksAsync(ct);

        return new SetupStatusDto
        {
            Bot = bot,
            Frameworks = frameworks,
            Checks =
            [
                AdminCheck(),
                KeyCheck(),
                BotCheck(bot),
                CatalogCheck(),
                FrameworksCheck(frameworks),
            ],
        };
    }

    public Task<BotSettingsDto> SaveBotAsync(BotSettingsUpdateDto dto, CancellationToken ct = default)
    {
        botSettings.Save(dto);
        return Task.FromResult(botSettings.Describe());
    }

    public Task<BotSettingsDto> RemoveBotAsync(CancellationToken ct = default)
    {
        botSettings.Remove();
        return Task.FromResult(botSettings.Describe());
    }

    public async Task<BotTestResultDto> TestBotAsync(CancellationToken ct = default)
    {
        if (!botSettings.IsConfigured)
            throw new InvalidOperationException("There is no bot account to test. Save one first.");

        var result = await f95.TestLoginAsync(ct);
        botSettings.LastTest = result;
        logger.LogInformation("F95zone bot test login: {Success}.", result.Success);
        return result;
    }

    // ── Checks ──────────────────────────────────────────────────────────────

    private SetupCheckDto AdminCheck()
    {
        var check = new SetupCheckDto { Title = "Admin account from the server settings" };
        if (adminAccount.UserName is not { } name)
        {
            check.State = SetupCheckState.Todo;
            check.Detail = "F3M_ADMIN_USERNAME is not set. Set it with F3M_ADMIN_PASSWORD so the site always has an admin, " +
                           "even if every admin is removed here.";
        }
        else if (adminAccount.Problem is { } problem)
        {
            check.State = SetupCheckState.Problem;
            check.Detail = $"{name}: {problem}";
        }
        else
        {
            check.State = SetupCheckState.Done;
            check.Detail = $"{name} is an admin at every start." +
                           (adminAccount.OverwritePassword
                               ? " F3M_ADMIN_PASSWORD_OVERWRITE is on: the password is reset at every start, so turn it off once you can sign in."
                               : string.Empty);
        }

        return check;
    }

    private SetupCheckDto KeyCheck()
    {
        var (ok, message) = catalog.CheckKey();
        return new SetupCheckDto
        {
            Title = "Catalog signing key",
            State = ok ? SetupCheckState.Done : SetupCheckState.Problem,
            Detail = ok
                ? message + " Back up Storage/Secrets together with Catalog__KeyPassword: desktop apps only trust this key."
                : message,
        };
    }

    private static SetupCheckDto BotCheck(BotSettingsDto bot)
    {
        var check = new SetupCheckDto { Title = "F95zone account linking" };
        var from = bot.Source == BotSettingsSource.Site ? "saved on this page" : "from the F95Bot settings";

        if (bot.Source == BotSettingsSource.None)
        {
            check.State = SetupCheckState.Todo;
            check.Detail = "No bot account: nobody can create an account or link an F95zone profile. Add one below.";
        }
        else if (bot.LastTest is null)
        {
            check.State = SetupCheckState.Todo;
            check.Detail = $"Bot account {bot.Username} ({from}) is not tested since the server started. Use \"Test login\" below.";
        }
        else if (!bot.LastTest.Success)
        {
            check.State = SetupCheckState.Problem;
            check.Detail = $"Bot account {bot.Username} ({from}) could not sign in: {bot.LastTest.Message}";
        }
        else
        {
            check.State = SetupCheckState.Done;
            check.Detail = $"Bot account {bot.Username} ({from}) signed in.";
        }

        return check;
    }

    private SetupCheckDto CatalogCheck()
    {
        var (writtenAt, mods) = catalog.IndexInfo();
        return writtenAt is { } at
            ? new SetupCheckDto
            {
                Title = "Public catalog",
                State = SetupCheckState.Done,
                Detail = $"Lists {mods} mod{(mods == 1 ? string.Empty : "s")}; last written {at:yyyy-MM-dd HH:mm} UTC.",
            }
            : new SetupCheckDto
            {
                Title = "Public catalog",
                State = SetupCheckState.Todo,
                Detail = "Not written yet. It is written with the first mod, or with \"Rebuild catalog\" on the Admin Panel.",
            };
    }

    private static SetupCheckDto FrameworksCheck(List<SetupFrameworkDto> frameworks)
    {
        var missing = frameworks.Where(f => f.ModId is null).Select(f => f.Name).ToList();
        return new SetupCheckDto
        {
            Title = "Frameworks",
            State = missing.Count == 0 ? SetupCheckState.Done : SetupCheckState.Todo,
            Detail = missing.Count == 0
                ? "Every mod the mod types need is on the site."
                : $"Missing: {string.Join(", ", missing)}. Uploads of these types can't name their framework as a requirement. Import them below.",
        };
    }

    /// <summary>Every framework, with the newest version on the site (by name, like the upload checks).</summary>
    private async Task<List<SetupFrameworkDto>> FrameworksAsync(CancellationToken ct)
    {
        var inPlan = import.FrameworkNamesInPlan();
        var result = new List<SetupFrameworkDto>();
        foreach (var (name, neededBy) in ModTemplates.Frameworks())
        {
            var lower = name.ToLower();
            var modId = await db.Mods.AsNoTracking()
                .Where(m => m.IsLatestVersion && m.IsApproved && m.Name.ToLower() == lower)
                .OrderByDescending(m => m.Id)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync(ct);

            result.Add(new SetupFrameworkDto
            {
                Name = name,
                NeededBy = [.. neededBy.Select(t => t.Name)],
                ModId = modId,
                InPlan = inPlan.Contains(name),
            });
        }

        return result;
    }
}
