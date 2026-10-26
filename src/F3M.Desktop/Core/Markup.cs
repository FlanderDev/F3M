using System.Text;
using System.Text.RegularExpressions;

namespace F3M.Desktop.Core;

/// <summary>
/// Text markup shown by the views: <c>**text**</c> is highlighted, <c>[[target]]</c> or <c>[[text|target]]</c> is a
/// clickable link or path.
/// </summary>
public static partial class Markup
{
    /// <summary>Builds markup for a link. The target is not checked here; <see cref="Launcher.Open"/> checks it on click.</summary>
    public static string Link(string target, string? text = null) =>
        text is null ? $"[[{Part(target)}]]" : $"[[{Part(text)}|{Part(target)}]]";

    /// <summary>Neutralises markup in text from outside the app, such as mod names and descriptions.</summary>
    public static string Escape(string? text) =>
        (text ?? string.Empty).Replace("**", "* *").Replace("[[", "[ [").Replace("]]", "] ]");

    /// <summary>
    /// Markup for a Markdown text from the server, such as a mod description: its http(s) links (<c>[text](url)</c>,
    /// <c>&lt;url&gt;</c> and bare addresses) become clickable, the rest is escaped and shown as written, without its
    /// <c>**bold**</c> markers.
    /// </summary>
    public static string FromMarkdown(string? markdown)
    {
        var source = markdown ?? string.Empty;
        var sb = new StringBuilder();
        var position = 0;
        foreach (Match match in MarkdownLinkPattern().Matches(source))
        {
            sb.Append(Plain(source[position..match.Index]));
            position = match.Index + match.Length;

            if (match.Groups["bare"].Success)
            {
                // Punctuation right after a bare address ends the sentence, it is not part of the link.
                var bare = match.Groups["bare"].Value;
                var url = bare.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '*', '_');
                sb.Append(Link(url)).Append(Plain(bare[url.Length..]));
                continue;
            }

            var text = Plain(match.Groups["text"].Value).Trim();
            sb.Append(text.Length == 0 ? Link(match.Groups["url"].Value) : Link(match.Groups["url"].Value, text));
        }

        sb.Append(Plain(source[position..]));
        return sb.ToString();

        static string Plain(string text) => Escape(text.Replace("**", string.Empty));
    }

    private static string Part(string text) => text.Replace("]", ")").Replace("|", "/");

    // [text](url "title") and ![alt](url), <url>, or a bare address. A url may hold one level of brackets,
    // as in https://en.wikipedia.org/wiki/Foo_(bar).
    [GeneratedRegex("""
        !?\[(?<text>[^\[\]\n]*)\]\(\s*<?(?<url>https?://[^\s()<>]+(?:\([^\s()<>]*\)[^\s()<>]*)*)>?(?:\s+"[^"\n]*")?\s*\)
        |<(?<url>https?://[^\s<>]+)>
        |(?<bare>https?://[^\s<>()\[\]]+(?:\([^\s()<>]*\)[^\s<>()\[\]]*)*)
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkPattern();
}
