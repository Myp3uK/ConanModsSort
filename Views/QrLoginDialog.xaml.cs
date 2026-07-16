using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QRCoder;

namespace ConanModsSort;

public partial class QrLoginDialog : Window
{
    private readonly SteamAccount _account;
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    /// <summary>Результат входа (для сохранения токена), если вход удался.</summary>
    public SteamLoginResult? LoginResult { get; private set; }

    public QrLoginDialog(SteamAccount account)
    {
        InitializeComponent();
        _account = account;
        Loaded += async (_, _) => await RunQrAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task RunQrAsync()
    {
        try
        {
            var result = await _account.LoginWithQrAsync(
                url => Dispatcher.Invoke(() => SetQr(url)), _cts.Token);
            Finish(result);
        }
        catch (OperationCanceledException) { /* отмена/переключение на пароль */ }
        catch (Exception ex)
        {
            if (_finished) return;
            Dispatcher.Invoke(() =>
            {
                txtStatus.Text = $"Ошибка: {ex.Message}";
                txtStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
            });
        }
    }

    private void SetQr(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);

        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();

        imgQr.Source = bmp;
        txtQrStatus.Visibility = Visibility.Collapsed;
        txtStatus.Text = "Отсканируйте код в приложении Steam…";
    }

    private void Finish(SteamLoginResult result)
    {
        Dispatcher.Invoke(() =>
        {
            _finished = true;
            LoginResult = result;
            DialogResult = true;
        });
    }

    private async void Password_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel(); // прекращаем QR-опрос
        var creds = LoginDialog.Ask(this);
        if (creds == null) return;

        btnPassword.IsEnabled = false;
        txtStatus.Text = "Вхожу по паролю…";
        try
        {
            var auth = new WpfAuthenticator(this, s => Dispatcher.Invoke(() => txtStatus.Text = s));
            var result = await _account.LoginWithCredentialsAsync(
                creds.Value.user, creds.Value.pass, auth, CancellationToken.None);
            _finished = true;
            LoginResult = result;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            btnPassword.IsEnabled = true;
            txtStatus.Text = "Вход отменён.";
        }
        catch (Exception ex)
        {
            btnPassword.IsEnabled = true;
            txtStatus.Text = $"Ошибка входа: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false;
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
