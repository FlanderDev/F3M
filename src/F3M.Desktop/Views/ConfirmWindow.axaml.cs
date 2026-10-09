using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace F3M.Desktop.Views;

/// <summary>A modal yes or no dialog. Every link and destructive step asks through here (plan 9.6).</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(false);
        OkButton.Click += (_, _) => Close(true);
    }

    /// <summary>Text between ** markers in <paramref name="body"/> is highlighted, for names the question is about.</summary>
    public static Task<bool> AskAsync(Window owner, string title, string body, string okText)
    {
        var window = new ConfirmWindow { Title = title };
        window.SetBody(body);
        window.OkButton.Content = okText;
        return window.ShowDialog<bool>(owner);
    }

    private void SetBody(string body)
    {
        var inlines = new InlineCollection();
        var accent = this.TryFindResource("F3M.Brush.Accent", out var brush) ? brush as IBrush : null;
        var parts = body.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            // Odd parts sit between markers; an unpaired marker leaves the rest as plain text.
            var highlighted = i % 2 == 1 && i < parts.Length - 1;
            inlines.Add(highlighted
                ? new Run(parts[i]) { FontWeight = FontWeight.SemiBold, Foreground = accent }
                : new Run(parts[i]));
        }

        BodyText.Inlines = inlines;
    }
}
