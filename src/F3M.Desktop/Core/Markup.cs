namespace F3M.Desktop.Core;

/// <summary>
/// Text markup shown by the views: <c>**text**</c> is highlighted, <c>[[target]]</c> or <c>[[text|target]]</c> is a
/// clickable link or path.
/// </summary>
public static class Markup
{
    /// <summary>Builds markup for a link. The target is not checked here; <see cref="Launcher.Open"/> checks it on click.</summary>
    public static string Link(string target, string? text = null) =>
        text is null ? $"[[{Part(target)}]]" : $"[[{Part(text)}|{Part(target)}]]";

    /// <summary>Neutralises markup in text from outside the app, such as mod names and descriptions.</summary>
    public static string Escape(string? text) =>
        (text ?? string.Empty).Replace("**", "* *").Replace("[[", "[ [").Replace("]]", "] ]");

    private static string Part(string text) => text.Replace("]", ")").Replace("|", "/");
}
