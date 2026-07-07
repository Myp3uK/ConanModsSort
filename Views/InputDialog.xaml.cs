using System.Windows;
using System.Windows.Input;

namespace ConanModsSort;

public partial class InputDialog : Window
{
    public string ResponseText => Input.Text.Trim();

    public InputDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true && dlg.ResponseText.Length > 0 ? dlg.ResponseText : null;
    }
}
