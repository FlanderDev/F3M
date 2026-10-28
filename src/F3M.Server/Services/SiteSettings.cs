using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace F3M.Server.Services;

/// <summary>Settings admins change on the site, kept in the SiteSettings table. A missing one has its default.</summary>
public sealed class SiteSettings(AppDbContext db)
{
    private const string TagSuggestionThresholdKey = "TagSuggestions.Threshold";
    private const string TagSuggestionDailyLimitKey = "TagSuggestions.DailyLimit";

    public async Task<TagSuggestionLimits> TagSuggestionLimitsAsync(CancellationToken ct = default)
    {
        var values = await db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == TagSuggestionThresholdKey || s.Key == TagSuggestionDailyLimitKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        return new TagSuggestionLimits
        {
            Threshold = Int(values, TagSuggestionThresholdKey) ?? TagSuggestionLimits.DefaultThreshold,
            DailyLimit = Int(values, TagSuggestionDailyLimitKey) ?? TagSuggestionLimits.DefaultDailyLimit
        };
    }

    public async Task<int> TagSuggestionThresholdAsync(CancellationToken ct = default) =>
        (await TagSuggestionLimitsAsync(ct)).Threshold;

    public async Task SetTagSuggestionLimitsAsync(TagSuggestionLimits limits, CancellationToken ct = default)
    {
        await SetAsync(TagSuggestionThresholdKey, limits.Threshold.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(TagSuggestionDailyLimitKey, limits.DailyLimit.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.SiteSettings.FindAsync([key], ct);
        if (setting is null)
            db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else
            setting.Value = value;
    }

    private static int? Int(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
