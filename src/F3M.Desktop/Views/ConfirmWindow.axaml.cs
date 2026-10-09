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

    public static Task<bool> AskAsync(Window owner, string title, string body, string okText)
    {
        var window = new ConfirmWindow { Title = title };
        window.BodyText.Text = body;
        window.OkButton.Content = okText;
        return window.ShowDialog<bool>(owner);
    }
}
