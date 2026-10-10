using Avalonia.Controls;

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

    /// <summary><paramref name="body"/> is markup (see <see cref="Links.MarkupProperty"/>): ** highlights names, [[ ]] makes links.</summary>
    public static Task<bool> AskAsync(Window owner, string title, string body, string okText)
    {
        var window = new ConfirmWindow { Title = title };
        window.SetBody(body);
        window.OkButton.Content = okText;
        return window.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Like <see cref="AskAsync"/>, with a check box under the text. The text is rebuilt when the box changes, so it
    /// can show what the choice means. Returns null when cancelled, otherwise whether the box was ticked.
    /// </summary>
    public static async Task<bool?> AskWithOptionAsync(Window owner, string title, Func<bool, string> body, string okText,
        string optionText, bool destructive)
    {
        var window = new ConfirmWindow { Title = title };
        window.SetBody(body(false));
        window.OkButton.Content = okText;
        if (destructive) window.OkButton.Classes.Add("danger");
        window.OptionBox.Content = optionText;
        window.OptionBox.IsVisible = true;
        window.OptionBox.IsCheckedChanged += (_, _) => window.SetBody(body(window.OptionBox.IsChecked == true));

        var ok = await window.ShowDialog<bool>(owner);
        return ok ? window.OptionBox.IsChecked == true : null;
    }

    private void SetBody(string body) => Links.SetMarkup(BodyText, body);
}
