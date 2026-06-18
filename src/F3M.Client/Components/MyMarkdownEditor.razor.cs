using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components;

public partial class MyMarkdownEditor
{
    [Parameter] public string Value { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public int MarkdownRows { get; set; } = 12;

    private ElementReference _editorRef;
    private int _selectionStart;
    private int _selectionEnd;

    private Task StoreSelection()
    {
        return Task.CompletedTask;
        //StateHasChanged();
    }

    private async Task ApplyChange(string newValue)
    {
        Value = newValue;
        await ValueChanged.InvokeAsync(Value);
    }

    private async Task WrapSelection(string prefix, string suffix)
    {
        var selected = GetSelectedText(Value);

        if (string.IsNullOrEmpty(selected))
        {
            selected = "text";
        }

        var replacement = prefix + selected + suffix;
        var result = ReplaceSelection(Value, replacement);
        await ApplyChange(result);
    }

    private async Task InsertHeading()
    {
        var result = InsertAtLineStart("## ");
        await ApplyChange(result);
    }

    private async Task InsertBulletList()
    {
        var result = InsertAtLineStart("- ");
        await ApplyChange(result);
    }

    private async Task InsertQuote()
    {
        var result = InsertAtLineStart("> ");
        await ApplyChange(result);
    }

    private async Task InsertLink()
    {
        var selected = GetSelectedText(Value);
        var label = string.IsNullOrWhiteSpace(selected) ? "link text" : selected;
        var replacement = $"[{label}](https://)";
        var result = ReplaceSelection(Value, replacement);
        await ApplyChange(result);
    }

    private string InsertAtLineStart(string token)
    {
        var start = Math.Max(0, Math.Min(_selectionStart, Value.Length));
        var lineStart = Value.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        return Value.Insert(lineStart, token);
    }

    private string GetSelectedText(string text)
    {
        var start = Math.Max(0, Math.Min(_selectionStart, text.Length));
        var end = Math.Max(0, Math.Min(_selectionEnd, text.Length));
        if (end < start) (start, end) = (end, start);
        if (end <= start)
            return string.Empty;

        return text[start..end];
    }

    private string ReplaceSelection(string text, string replacement)
    {
        var start = Math.Max(0, Math.Min(_selectionStart, text.Length));
        var end = Math.Max(0, Math.Min(_selectionEnd, text.Length));
        if (end < start) (start, end) = (end, start);

        if (end > start)
            return text[..start] + replacement + text[end..];

        return text.Insert(start, replacement);
    }
}