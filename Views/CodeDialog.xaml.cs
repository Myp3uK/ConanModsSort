using System.Windows;
using System.Windows.Input;

namespace ConanModsSort;

public partial class CodeDialog : Window
{
    public string Code { get; private set; } = "";

    public CodeDialog(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Loaded += (_, _) => txtCode.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var code = txtCode.Text.Trim();
        if (string.IsNullOrEmpty(code)) return;
        Code = code;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Показывает диалог ввода кода; возвращает код или null при отмене.</summary>
    public static string? Ask(Window? owner, string prompt)
    {
        var dlg = new CodeDialog(prompt) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Code : null;
    }
}
