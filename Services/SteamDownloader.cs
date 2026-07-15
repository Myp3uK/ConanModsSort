using System.IO;
using System.Net.Http;
using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;
using CdnClient = SteamKit2.CDN.Client;

namespace ConanModsSort;

/// <summary>
/// Скачивание модов Steam Workshop напрямую (без steamcmd) через SteamKit2:
/// анонимный логин, получение манифеста и чанков с CDN Valve, сборка файлов.
/// </summary>
public sealed class SteamDownloader : IDisposable
{
    public const uint ConanAppId = 440900;

    private readonly SteamClient _client;
    private readonly CallbackManager _manager;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamContent _content;
    private readonly SteamUnifiedMessages _unified;

    private readonly object _lock = new();
    private Thread? _pump;
    private volatile bool _running;
    private volatile bool _loggedOn;
    private TaskCompletionSource<bool>? _loginTcs;

    private readonly Dictionary<uint, byte[]> _depotKeys = new();

    public SteamDownloader()
    {
        _client = new SteamClient();
        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;
        _unified = _client.GetHandler<SteamUnifiedMessages>()!;

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ => { lock (_lock) _user.LogOnAnonymous(); });
        _manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            _loggedOn = false;
            _loginTcs?.TrySetException(new Exception("Соединение со Steam разорвано."));
        });
        _manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            if (cb.Result == EResult.OK) { _loggedOn = true; _loginTcs?.TrySetResult(true); }
            else _loginTcs?.TrySetException(new Exception($"Steam login: {cb.Result}"));
        });
    }

    /// <summary>Скачивает воркшоп-мод в &lt;destRoot&gt;/&lt;id&gt;. progress — 0..1.</summary>
    public async Task DownloadWorkshopItemAsync(ulong pubFileId, string destDir,
        IProgress<double>? progress, CancellationToken ct)
    {
        await EnsureLoggedInAsync(ct);

        // 1. Детали пабфайла
        var svc = _unified.CreateService<PublishedFile>();
        var req = new CPublishedFile_GetDetails_Request { appid = ConanAppId };
        req.publishedfileids.Add(pubFileId);
        var det = await Locked(() => svc.GetDetails(req));
        if (det.Result != EResult.OK)
            throw new Exception($"Не удалось получить данные мода ({det.Result}).");
        var pf = det.Body.publishedfiledetails.FirstOrDefault()
                 ?? throw new Exception("Мод не найден в Workshop.");

        Directory.CreateDirectory(destDir);

        // 2а. Web-file (одиночный файл по URL)
        if (!string.IsNullOrEmpty(pf.file_url))
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(pf.file_url, ct);
            var name = string.IsNullOrEmpty(pf.filename) ? pubFileId.ToString() : Path.GetFileName(pf.filename);
            await File.WriteAllBytesAsync(Path.Combine(destDir, name), bytes, ct);
            progress?.Report(1.0);
            return;
        }

        ulong manifestId = pf.hcontent_file;
        if (manifestId == 0) throw new Exception("У мода нет контента (пустой манифест).");

        // 2б. Depot-контент: узнаём workshop-депо игры
        var pics = await Locked(() => _apps.PICSGetProductInfo(new SteamApps.PICSRequest(ConanAppId), null));
        var appInfo = pics.Results!.First().Apps[ConanAppId].KeyValues;
        uint depotId = appInfo["depots"]["workshopdepot"].AsUnsignedInteger();
        if (depotId == 0) throw new Exception("Не найден workshopdepot для Conan Exiles.");

        byte[] depotKey = await GetDepotKeyAsync(depotId, ct);

        ulong code = await _content.GetManifestRequestCode(depotId, ConanAppId, manifestId);
        var servers = (await _content.GetServersForSteamPipe()).ToList();
        if (servers.Count == 0) throw new Exception("Steam не вернул CDN-серверов.");

        var cdn = new CdnClient(_client);
        var manifest = await DownloadManifestAsync(cdn, depotId, manifestId, code, servers, depotKey, ct);

        var files = (manifest.Files ?? new List<DepotManifest.FileData>())
            .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
            .ToList();

        long total = files.Sum(f => (long)f.TotalSize);
        long done = 0;

        foreach (var f in files)
        {
            var p = Path.Combine(destDir, f.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        }

        using var sem = new SemaphoreSlim(6);
        foreach (var file in files)
        {
            var path = Path.Combine(destDir, file.FileName);
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None,
                FileOptions.None, (long)file.TotalSize);

            var tasks = file.Chunks.Select(async chunk =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var buffer = new byte[chunk.UncompressedLength];
                    int written = await DownloadChunkAsync(cdn, depotId, chunk, servers, buffer, depotKey, ct);
                    RandomAccess.Write(handle, buffer.AsSpan(0, written), (long)chunk.Offset);
                    long d = Interlocked.Add(ref done, written);
                    progress?.Report(total > 0 ? Math.Min(1.0, (double)d / total) : 1.0);
                }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        progress?.Report(1.0);
    }

    // ---------- CDN с ротацией серверов и ретраями ----------

    private static async Task<DepotManifest> DownloadManifestAsync(CdnClient cdn, uint depotId,
        ulong manifestId, ulong code, List<Server> servers, byte[] key, CancellationToken ct)
    {
        Exception? last = null;
        int attempts = Math.Min(10, servers.Count * 2);
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await cdn.DownloadManifestAsync(depotId, manifestId, code, servers[i % servers.Count], key); }
            catch (Exception ex) { last = ex; await Task.Delay(300, ct); }
        }
        throw last ?? new Exception("Не удалось скачать манифест.");
    }

    private static async Task<int> DownloadChunkAsync(CdnClient cdn, uint depotId,
        DepotManifest.ChunkData chunk, List<Server> servers, byte[] buffer, byte[] key, CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 0; i < 6; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await cdn.DownloadDepotChunkAsync(depotId, chunk, servers[i % servers.Count], buffer, key); }
            catch (Exception ex) { last = ex; await Task.Delay(200, ct); }
        }
        throw last ?? new Exception("Не удалось скачать чанк.");
    }

    // ---------- Сессия ----------

    private async Task<byte[]> GetDepotKeyAsync(uint depotId, CancellationToken ct)
    {
        if (_depotKeys.TryGetValue(depotId, out var cached)) return cached;
        var cb = await Locked(() => _apps.GetDepotDecryptionKey(depotId, ConanAppId));
        if (cb.Result != EResult.OK)
            throw new Exception($"Нет ключа депо {depotId} ({cb.Result}).");
        _depotKeys[depotId] = cb.DepotKey;
        return cb.DepotKey;
    }

    public async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        if (_loggedOn) return;
        StartPump();
        _loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _client.Connect();
        await _loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
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
        { IsBackground = true, Name = "SteamKit-pump" };
        _pump.Start();
    }

    /// <summary>Вызов метода-хендлера под локом (SteamClient не потокобезопасен), await — снаружи.</summary>
    private T Locked<T>(Func<T> submit)
    {
        lock (_lock) return submit();
    }

    public void Dispose()
    {
        _running = false;
        try { lock (_lock) { _user.LogOff(); _client.Disconnect(); } } catch { }
    }
}
