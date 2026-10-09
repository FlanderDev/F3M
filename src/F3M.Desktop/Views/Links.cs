using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using F3M.Desktop.Core;

namespace F3M.Desktop.Views;

/// <summary>
/// Clickable links and paths in TextBlocks. Opening is done by <see cref="Launcher"/>; targets it would not open
/// stay plain text.
/// </summary>
public static partial class Links
{
    /// <summary>Makes a whole TextBlock a link to this target. Empty means plain text.</summary>
    public static readonly AttachedProperty<string?> TargetProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Target", typeof(Links));

    /// <summary>
    /// Sets a TextBlock's text from markup: <c>**text**</c> is highlighted, <c>[[target]]</c> or <c>[[text|target]]</c>
    /// is a link. Text from outside the app goes through <see cref="Markup.Escape"/> first.
    /// </summary>
    public static readonly AttachedProperty<string?> MarkupProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Markup", typeof(Links));

    static Links()
    {
        TargetProperty.Changed.AddClassHandler<TextBlock>((block, _) => ApplyTarget(block));
        MarkupProperty.Changed.AddClassHandler<TextBlock>((block, e) => Render(block, e.NewValue as string));
    }

    public static string? GetTarget(TextBlock block) => block.GetValue(TargetProperty);
    public static void SetTarget(TextBlock block, string? value) => block.SetValue(TargetProperty, value);
    public static string? GetMarkup(TextBlock block) => block.GetValue(MarkupProperty);
    public static void SetMarkup(TextBlock block, string? value) => block.SetValue(MarkupProperty, value);

    private static void ApplyTarget(TextBlock block)
    {
        block.PointerReleased -= OnLinkReleased;
        var openable = Launcher.IsOpenable(GetTarget(block));
        block.Classes.Set("link", openable);
        if (!openable) return;

        block.PointerReleased += OnLinkReleased;
        ToolTip.SetTip(block, GetTarget(block));
    }

    private static void OnLinkReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not TextBlock block || GetTarget(block) is not { } target) return;
        e.Handled = true;
        Launcher.Open(target);
    }

    private static void Render(TextBlock block, string? markup)
    {
        var inlines = new InlineCollection();
        var accent = block.TryFindResource("F3M.Brush.Accent", out var brush) ? brush as IBrush : null;
        var position = 0;
        foreach (Match match in MarkupPattern().Matches(markup ?? string.Empty))
        {
            if (match.Index > position) inlines.Add(new Run(markup![position..match.Index]));
            position = match.Index + match.Length;

            if (match.Groups["bold"].Success)
            {
                inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeight.SemiBold, Foreground = accent });
                continue;
            }

            var target = match.Groups["target"].Value;
            var text = match.Groups["text"].Success ? match.Groups["text"].Value : target;
            if (!Launcher.IsOpenable(target))
            {
                inlines.Add(new Run(text));
                continue;
            }

            var link = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            SetTarget(link, target);
            inlines.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
        }

        if (markup is not null && position < markup.Length) inlines.Add(new Run(markup[position..]));
        block.Inlines = inlines;
    }

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|\[\[(?:(?<text>[^|\]]+)\|)?(?<target>[^|\]]+)\]\]", RegexOptions.Singleline)]
    private static partial Regex MarkupPattern();
}
