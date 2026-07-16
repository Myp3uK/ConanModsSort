using System.Windows;
using SteamKit2.Authentication;

namespace ConanModsSort;

/// <summary>
/// Steam Guard-аутентификатор для SteamKit: запрашивает код у пользователя через диалог,
/// а подтверждение в мобильном приложении — опросом (пуш в приложении Steam).
/// </summary>
public sealed class WpfAuthenticator : IAuthenticator
{
    private readonly Window _owner;
    private readonly Action<string>? _status;

    public WpfAuthenticator(Window owner, Action<string>? status = null)
    {
        _owner = owner;
        _status = status;
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        var prompt = previousCodeWasIncorrect
            ? $"Код неверный. Введите код Steam Guard из письма ({email})."
            : $"Введите код Steam Guard, отправленный на почту {email}.";
        return AskCode(prompt);
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        var prompt = previousCodeWasIncorrect
            ? "Код неверный. Введите текущий код из приложения Steam Guard."
            : "Введите код из мобильного приложения Steam Guard.";
        return AskCode(prompt);
    }

    // true — опрашивать до подтверждения в мобильном приложении (пуш-подтверждение входа).
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        _status?.Invoke("Подтвердите вход в мобильном приложении Steam…");
        return Task.FromResult(true);
    }

    private Task<string> AskCode(string prompt)
    {
        var code = _owner.Dispatcher.Invoke(() => CodeDialog.Ask(_owner, prompt));
        if (string.IsNullOrEmpty(code))
            throw new OperationCanceledException("Вход отменён пользователем.");
        return Task.FromResult(code);
    }
}
