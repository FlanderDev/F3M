using System.Globalization;
using System.Text.RegularExpressions;

namespace F3M.Desktop.Services;

/// <summary>How the editor shows a setting. Derived from the BepInEx comment block.</summary>
public enum CfgKind
{
    Text,
    Boolean,
    Number,
    Choice,
}

/// <summary>One "Key = value" line with the description, type and default that BepInEx wrote above it.</summary>
public sealed class CfgSetting
{
    public required string Section { get; init; }
    public required string Key { get; init; }
    public required string Description { get; init; }
    public required string SettingType { get; init; }
    public string? DefaultValue { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public required IReadOnlyList<string> Choices { get; init; }
    public required int LineIndex { get; init; }
    public required string OriginalValue { get; init; }

    /// <summary>The value as it will be written. Kept in step with the document by <see cref="CfgDocument.SetValue"/>.</summary>
    public string Value { get; set; } = string.Empty;

    public CfgKind Kind
    {
        get
        {
            if (Choices.Count > 0) return CfgKind.Choice;
            if (SettingType.Equals("Boolean", StringComparison.OrdinalIgnoreCase)) return CfgKind.Boolean;
            if (Min is not null || Max is not null || IsNumericType(SettingType)) return CfgKind.Number;
            return CfgKind.Text;
        }
    }

    public bool ChangedFromDefault =>
        DefaultValue is not null && !string.Equals(Value, DefaultValue, StringComparison.OrdinalIgnoreCase);

    public bool IsChanged => !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    /// <summary>A message when the value is not allowed for this setting, or null when it is.</summary>
    public string? Validate(string value)
    {
        switch (Kind)
        {
            case CfgKind.Boolean:
                return value is "true" or "false" or "True" or "False" ? null : "Use true or false.";

            case CfgKind.Choice:
                return Choices.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : $"Use one of: {string.Join(", ", Choices)}.";

            case CfgKind.Number:
                if (SettingType.StartsWith("Int", StringComparison.OrdinalIgnoreCase)
                    || SettingType.Contains("Byte", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                        return "Enter a whole number.";
                    return OutOfRange(whole);
                }

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return "Enter a number.";
                return OutOfRange(number);

            default:
                return null;
        }
    }

    private string? OutOfRange(double number)
    {
        if (Min is not null && Max is not null && (number < Min || number > Max))
            return $"Enter a value from {Min} to {Max}.";
        if (Min is not null && number < Min) return $"Enter a value of {Min} or more.";
        if (Max is not null && number > Max) return $"Enter a value of {Max} or less.";
        return null;
    }

    private static bool IsNumericType(string type) =>
        type is "Single" or "Double" or "Decimal"
            || type.StartsWith("Int", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("UInt", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Byte", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A BepInEx .cfg file kept as lines. Only the value text of a changed entry is rewritten, so comments, blank lines,
/// key order, and unknown lines come back exactly as they were (plan 8.3).
/// </summary>
public sealed class CfgDocument
{
    private static readonly Regex RangeRegex = new(
        @"From\s+(?<min>[-+0-9.eE]+)\s+to\s+(?<max>[-+0-9.eE]+)",
        RegexOptions.CultureInvariant);

    private readonly List<string> _lines;
    private readonly string _eol;
    private readonly List<CfgSetting> _settings = [];

    private CfgDocument(List<string> lines, string eol)
    {
        _lines = lines;
        _eol = eol;
    }

    public IReadOnlyList<CfgSetting> Settings => _settings;

    public static CfgDocument Parse(string text)
    {
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var document = new CfgDocument(lines, eol);
        document.Scan();
        return document;
    }

    public void SetValue(CfgSetting setting, string value)
    {
        var line = _lines[setting.LineIndex];
        var equals = line.IndexOf('=');
        var afterEquals = line[(equals + 1)..];
        var leading = afterEquals.Length - afterEquals.TrimStart().Length;
        _lines[setting.LineIndex] = line[..(equals + 1)] + afterEquals[..leading] + value;
        setting.Value = value;
    }

    public string ToText() => string.Join(_eol, _lines);

    private void Scan()
    {
        var section = string.Empty;
        var description = new List<string>();
        string? type = null, defaultValue = null, range = null, values = null;

        void Reset()
        {
            description.Clear();
            type = defaultValue = range = values = null;
        }

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                Reset();
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1];
                Reset();
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                if (trimmed.StartsWith("##")) description.Add(trimmed[2..].Trim());
                else if (trimmed.StartsWith("# Setting type:")) type = Rest(trimmed, "# Setting type:");
                else if (trimmed.StartsWith("# Default value:")) defaultValue = Rest(trimmed, "# Default value:");
                else if (trimmed.StartsWith("# Acceptable value range:")) range = Rest(trimmed, "# Acceptable value range:");
                else if (trimmed.StartsWith("# Acceptable values:")) values = Rest(trimmed, "# Acceptable values:");
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Length > 0)
            {
                var value = line[(equals + 1)..].Trim();
                double? min = null, max = null;
                if (range is not null)
                {
                    var match = RangeRegex.Match(range);
                    if (match.Success
                        && double.TryParse(match.Groups["min"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
                        && double.TryParse(match.Groups["max"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi))
                    {
                        min = lo;
                        max = hi;
                    }
                }

                var choices = values is null
                    ? new List<string>()
                    : values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

                _settings.Add(new CfgSetting
                {
                    Section = section,
                    Key = line[..equals].Trim(),
                    Description = string.Join(" ", description),
                    SettingType = type ?? string.Empty,
                    DefaultValue = defaultValue,
                    Min = min,
                    Max = max,
                    Choices = choices,
                    LineIndex = i,
                    OriginalValue = value,
                    Value = value,
                });
            }

            // Anything else (including unknown lines) is kept as it is.
            Reset();
        }
    }

    private static string Rest(string line, string prefix) => line[prefix.Length..].Trim();
}
