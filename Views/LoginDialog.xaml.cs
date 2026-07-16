using System.Windows;
using System.Windows.Input;

namespace ConanModsSort;

public partial class LoginDialog : Window
{
    public string Username { get; private set; } = "";
    public string Password { get; private set; } = "";

    public LoginDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => txtUser.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var user = txtUser.Text.Trim();
        var pass = txtPass.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            MessageDialog.Show(this, "Вход в Steam", "Введите логин и пароль.", MessageButtons.Ok, MessageKind.Warning);
            return;
        }
        Username = user;
        Password = pass;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Показывает диалог; возвращает (логин, пароль) или null при отмене.</summary>
    public static (string user, string pass)? Ask(Window? owner)
    {
        var dlg = new LoginDialog { Owner = owner };
        return dlg.ShowDialog() == true ? (dlg.Username, dlg.Password) : null;
    }
}
