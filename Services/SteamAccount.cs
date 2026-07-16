using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace ConanModsSort;

/// <summary>Результат входа: имя аккаунта и refresh-токен для беспарольного входа в будущем.</summary>
public readonly record struct SteamLoginResult(string AccountName, string RefreshToken);

/// <summary>
/// Авторизованная сессия Steam (по логину/паролю с 2FA или по сохранённому refresh-токену)
/// для подписки на воркшоп-моды — то, что делает кнопка «Subscribe» в самой игре.
/// </summary>
public sealed class SteamAccount : IDisposable
{
    public const uint ConanAppId = 440900;

    private readonly SteamClient _client;
    private readonly CallbackManager _manager;
    private readonly SteamUser _user;
    private readonly SteamUnifiedMessages _unified;

    private readonly object _lock = new();
    private Thread? _pump;
    private volatile bool _running;
    private volatile bool _loggedOn;

    private TaskCompletionSource<bool>? _connectedTcs;
    private TaskCompletionSource<bool>? _loginTcs;

    public bool IsLoggedOn => _loggedOn;

    public SteamAccount()
    {
        _client = new SteamClient();
        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _unified = _client.GetHandler<SteamUnifiedMessages>()!;

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ => _connectedTcs?.TrySetResult(true));
        _manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            _loggedOn = false;
            _connectedTcs?.TrySetException(new Exception("Соединение со Steam разорвано."));
            _loginTcs?.TrySetException(new Exception("Соединение со Steam разорвано."));
        });
        _manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            if (cb.Result == EResult.OK) { _loggedOn = true; _loginTcs?.TrySetResult(true); }
            else _loginTcs?.TrySetException(new Exception(LoginError(cb.Result)));
        });
    }

    /// <summary>Вход по логину/паролю. 2FA-коды запрашиваются через authenticator.
    /// Возвращает refresh-токен, который стоит сохранить для входа без пароля.</summary>
    public async Task<SteamLoginResult> LoginWithCredentialsAsync(
        string username, string password, IAuthenticator authenticator, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = true,
            Authenticator = authenticator,
        });

        var poll = await session.PollingWaitForResultAsync(ct);

        await LogOnAsync(poll.AccountName, poll.RefreshToken, ct);
        return new SteamLoginResult(poll.AccountName, poll.RefreshToken);
    }

    /// <summary>Вход по QR-коду (скан в мобильном приложении Steam). onChallengeUrl вызывается
    /// с текущим URL для QR — и при каждом его обновлении. Возвращает refresh-токен.</summary>
    public async Task<SteamLoginResult> LoginWithQrAsync(
        Action<string> onChallengeUrl, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var session = await _client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            IsPersistentSession = true,
        });

        onChallengeUrl(session.ChallengeURL);
        session.ChallengeURLChanged = () => onChallengeUrl(session.ChallengeURL);

        var poll = await session.PollingWaitForResultAsync(ct);

        await LogOnAsync(poll.AccountName, poll.RefreshToken, ct);
        return new SteamLoginResult(poll.AccountName, poll.RefreshToken);
    }

    /// <summary>Беспарольный вход по ранее сохранённому refresh-токену.</summary>
    public async Task LoginWithTokenAsync(string accountName, string refreshToken, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        await LogOnAsync(accountName, refreshToken, ct);
    }

    private async Task LogOnAsync(string accountName, string refreshToken, CancellationToken ct)
    {
        _loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _user.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true,
            });
        await _loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>Подписывает аккаунт на указанные воркшоп-моды. progress — (готово, всего).</summary>
    public async Task<int> SubscribeAsync(IEnumerable<ulong> pubFileIds,
        IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        if (!_loggedOn) throw new InvalidOperationException("Нет входа в Steam.");

        var svc = _unified.CreateService<PublishedFile>();
        var ids = pubFileIds.ToList();
        int done = 0, ok = 0;

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var req = new CPublishedFile_Subscribe_Request
            {
                publishedfileid = id,
                appid = (int)ConanAppId,
                notify_client = true,
                list_type = 1,
            };
            try
            {
                var resp = await Locked(() => svc.Subscribe(req));
                if (resp.Result == EResult.OK) ok++;
            }
            catch { /* пропускаем неудачные, продолжаем */ }

            progress?.Report((++done, ids.Count));
        }

        return ok;
    }

    // ---------- Сессия ----------

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        StartPump();
        if (_client.IsConnected) return;
        _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _client.Connect();
        await _connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private void StartPump()
    {
        if (_pump != null) return;
        _running = true;
        _pump = new Thread(() =>
        {
            while (_running)
            {
                lock (_lock) _manager.RunCallbacks();
                Thread.Sleep(5);
            }
        })
        { IsBackground = true, Name = "SteamAccount-pump" };
        _pump.Start();
    }

    private T Locked<T>(Func<T> submit)
    {
        lock (_lock) return submit();
    }

    private static string LoginError(EResult r) => r switch
    {
        EResult.InvalidPassword => "Неверный логин или пароль (либо истёк токен).",
        EResult.AccountLoginDeniedNeedTwoFactor => "Нужен код Steam Guard.",
        EResult.AccountLogonDenied => "Требуется код из письма Steam Guard.",
        EResult.RateLimitExceeded => "Слишком много попыток входа, подождите.",
        _ => $"Ошибка входа в Steam: {r}",
    };

    public void Dispose()
    {
        _running = false;
        try { lock (_lock) { _user.LogOff(); _client.Disconnect(); } } catch { }
    }
}
