using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ConanModsSort;

public partial class MainWindow : Window
{
    private sealed class VersionLists
    {
        public ObservableCollection<ModItem> Available { get; } = new();
        public ObservableCollection<ModItem> Ordered { get; } = new();
    }

    private readonly AppSettings _settings;
    private readonly VersionLists _enhanced = new();
    private readonly VersionLists _legacy = new();
    private VersionLists _current;
    private ModPreset? _loadedPreset;
    private List<string>? _presetBaseline;
    private List<string>? _modlistBaseline;

    private Point _dragStart;
    private ModItem? _dragItem;
    private ListBox? _dragSource;
    private DragAdorner? _adorner;
    private AdornerLayer? _adornerLayer;
    private InsertionAdorner? _insertion;
    private ListBox? _insertionList;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _current = _enhanced;
        lstAvailable.ItemsSource = _current.Available;
        lstOrdered.ItemsSource = _current.Ordered;
        cmbPreset.ItemsSource = _settings.Presets;

        Loaded += (_, _) => UpdateSteamUi();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_loadedPreset != null)
        {
            if (IsPresetModified())
            {
                var r = MessageDialog.Show(this, "Несохранённые изменения",
                    $"Профиль «{_loadedPreset.Name}» изменён, но не сохранён.\n\nСохранить перед выходом?",
                    MessageButtons.YesNoCancel, MessageKind.Warning);

                if (r == MessageResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageResult.Yes) SaveLoadedPreset();
            }
        }
        else if (IsModlistModified())
        {
            var r = MessageDialog.Show(this, "Несохранённые изменения",
                "Список модов изменён, но не применён к modlist.txt.\n\nПрименить перед выходом?",
                MessageButtons.YesNoCancel, MessageKind.Warning);

            if (r == MessageResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageResult.Yes) WriteModlist();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _steam?.Dispose();
        _account?.Dispose();
        base.OnClosed(e);
    }

    private void SaveLoadedPreset()
    {
        if (_loadedPreset == null) return;
        _loadedPreset.ModIds = PresetLists.Ordered.Select(m => m.ModId).ToList();
        _settings.Save();
        SetPresetBaseline();
        RefreshPresetDirty();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        WindowBorder.BorderThickness = new Thickness(max ? 0 : 1);
        WindowBorder.Padding = new Thickness(max ? 7 : 0);
        btnMax.Content = max ? "" : "";
        btnMax.ToolTip = max ? "Восстановить" : "Развернуть";
    }

    private void Enhanced_Checked(object sender, RoutedEventArgs e) => SetVersion(_enhanced);
    private void Legacy_Checked(object sender, RoutedEventArgs e) => SetVersion(_legacy);

    private void SetVersion(VersionLists lists)
    {
        if (ReferenceEquals(_current, lists)) return;
        _current = lists;
        if (lstAvailable == null || lstOrdered == null) return;
        lstAvailable.ItemsSource = _current.Available;
        lstOrdered.ItemsSource = _current.Ordered;
        UpdateStatus();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = cmbPreset.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowToast("Введите имя профиля в поле «Профиль», затем нажмите «Сохранить».", ToastKind.Warning);
            return;
        }

        var isEnh = ReferenceEquals(_current, _enhanced);
        var ids = _current.Ordered.Select(m => m.ModId).ToList();

        var preset = _settings.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset != null)
        {
            preset.ModIds = ids;
            preset.IsEnhanced = isEnh;
        }
        else
        {
            preset = new ModPreset { Name = name, IsEnhanced = isEnh, ModIds = ids };
            _settings.Presets.Add(preset);
        }

        _settings.Save();
        RefreshPresetCombo(preset);
        _loadedPreset = preset;
        SetPresetBaseline();
        RefreshPresetDirty();
        ShowToast($"Профиль «{name}» сохранён: {ids.Count} модов ({(isEnh ? "Enhanced" : "Legacy")}).",
            ToastKind.Success);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (cmbPreset.SelectedItem is not ModPreset p)
        {
            ShowToast("Выберите профиль из списка, чтобы удалить.", ToastKind.Warning);
            return;
        }

        if (MessageDialog.Show(this, "Профиль", $"Удалить профиль «{p.Name}»?",
                MessageButtons.YesNo, MessageKind.Question) != MessageResult.Yes)
            return;

        bool wasLoaded = ReferenceEquals(_loadedPreset, p);
        _settings.Presets.Remove(p);
        if (wasLoaded) { _loadedPreset = null; _presetBaseline = null; }
        _settings.Save();
        RefreshPresetCombo(null);
        cmbPreset.Text = "";
        ShowToast($"Профиль «{p.Name}» удалён.", ToastKind.Info);

        if (wasLoaded)
            _ = ReloadAsync();   // список профиля был открыт — возвращаем к применённому modlist.txt
        else
            RefreshPresetDirty();
    }

    private void Preset_DropDownClosed(object sender, EventArgs e)
    {
        if (cmbPreset.SelectedItem is ModPreset p && !ReferenceEquals(p, _loadedPreset))
            LoadPreset(p);
    }

    private async void LoadPreset(ModPreset p)
    {
        var global = _enhanced.Available.Concat(_enhanced.Ordered)
            .Concat(_legacy.Available).Concat(_legacy.Ordered)
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // «нет мода» = нет в списках ИЛИ есть только как плейсхолдер «не скачан»
        bool Have(string id) => global.TryGetValue(id, out var m) && m.Downloaded;
        var missingIds = p.ModIds.Where(id => !Have(id)).ToList();

        // данные из Steam тянем только для новых (которых ещё нет в списках)
        var toFetch = missingIds.Where(id => !global.ContainsKey(id)).ToList();
        var missDetails = new Dictionary<string, SteamWorkshopApi.Details>();
        if (toFetch.Count > 0)
        {
            try { missDetails = await SteamWorkshopApi.GetDetailsAsync(toFetch); } catch { }
        }

        // недостающие моды профиля — плейсхолдеры «не скачан» в версии профиля
        var placeholders = new List<ModItem>();
        foreach (var id in missingIds)
        {
            if (global.TryGetValue(id, out var existing)) { placeholders.Add(existing); continue; }
            var ph = new ModItem(id, "", downloaded: false) { IsEnhanced = p.IsEnhanced };
            if (missDetails.TryGetValue(id, out var d))
            {
                if (!string.IsNullOrWhiteSpace(d.Title)) ph.Title = d.Title;
                ph.PreviewUrl = d.PreviewUrl;
                ph.FileSize = d.FileSize;
            }
            (p.IsEnhanced ? _enhanced : _legacy).Available.Add(ph);
            placeholders.Add(ph);
        }

        ApplyOrderToVersion(p.IsEnhanced, p.ModIds);
        _loadedPreset = p;
        SetPresetBaseline();
        RefreshPresetDirty();

        if (placeholders.Count > 0)
        {
            _ = LoadCachedThumbnailsAsync(placeholders);
            _ = DownloadThumbnailsAsync(placeholders);
        }

        int total = p.ModIds.Count, missing = missingIds.Count, ready = total - missing;
        void Redownload() => DownloadMissing(missingIds, onComplete: () => LoadPreset(p));

        if (missing > 0)
        {
            txtStatus.Text = $"Профиль «{p.Name}»: {ready} готовы, {missing} не скачано. " +
                             "Нажмите «Скачать», затем «Применить».";
            ShowToast(
                $"Профиль «{p.Name}»: {ready} готовы, {missing} не скачано.",
                ToastKind.Info,
                actionText: $"Скачать ({missing})",
                action: Redownload, seconds: 10);
        }
        else
        {
            txtStatus.Text = $"Профиль «{p.Name}» загружен. Нажмите «Применить», чтобы записать в modlist.txt.";
        }
    }

    private int ApplyOrderToVersion(bool enhanced, IEnumerable<string> orderedIds)
    {
        if (enhanced) tabEnhanced.IsChecked = true;
        else tabLegacy.IsChecked = true;

        var lists = enhanced ? _enhanced : _legacy;
        var all = lists.Available.Concat(lists.Ordered)
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        lists.Ordered.Clear();
        lists.Available.Clear();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in orderedIds)
            if (all.TryGetValue(id, out var m) && used.Add(id))
                lists.Ordered.Add(m);

        foreach (var m in all.Values
                     .Where(x => !used.Contains(x.ModId))
                     .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
            lists.Available.Add(m);

        UpdateStatus();
        return used.Count;
    }

    private void RefreshPresetCombo(ModPreset? select)
    {
        cmbPreset.ItemsSource = null;
        cmbPreset.ItemsSource = _settings.Presets;
        if (select != null) cmbPreset.SelectedItem = select;
    }

    private VersionLists PresetLists =>
        (_loadedPreset?.IsEnhanced ?? true) ? _enhanced : _legacy;

    private void SetPresetBaseline()
    {
        _presetBaseline = _loadedPreset == null
            ? null
            : PresetLists.Ordered.Select(m => m.ModId).ToList();
    }

    private bool IsPresetModified()
    {
        if (_loadedPreset == null || _presetBaseline == null) return false;
        return !PresetLists.Ordered.Select(m => m.ModId)
            .SequenceEqual(_presetBaseline, StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshPresetDirty() =>
        txtPresetDirty.Visibility = IsPresetModified() ? Visibility.Visible : Visibility.Collapsed;

    private List<string> OrderSignature() =>
        _enhanced.Ordered.Select(m => "E:" + m.ModId)
            .Concat(_legacy.Ordered.Select(m => "L:" + m.ModId))
            .ToList();

    private void SetModlistBaseline() => _modlistBaseline = OrderSignature();

    private bool IsModlistModified() =>
        _modlistBaseline != null && !OrderSignature().SequenceEqual(_modlistBaseline);

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Ordered.Count == 0)
        {
            ShowToast("Список порядка пуст — нечего копировать.", ToastKind.Warning);
            return;
        }

        var version = ReferenceEquals(_current, _enhanced) ? "Enhanced" : "Legacy";
        var text = string.Join(",", _current.Ordered.Select(m => m.ModId));

        try
        {
            Clipboard.SetText(text);
            txtStatus.Text = $"Скопировано в буфер: {_current.Ordered.Count} модов ({version}).";
            ShowToast($"Скопировано в буфер: {_current.Ordered.Count} модов ({version}). Можно отправить другу.",
                ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось скопировать в буфер: {ex.Message}", ToastKind.Warning, seconds: 6);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (Exception ex)
        {
            ShowToast($"Не удалось прочитать буфер обмена: {ex.Message}", ToastKind.Warning, seconds: 6);
            return;
        }

        var (ids, headerEnhanced) = ParseSharedIds(text);
        if (ids.Count == 0)
        {
            ShowToast("В буфере обмена не найдено ID модов (ожидается список Workshop-ID).", ToastKind.Warning);
            return;
        }

        ApplyParsedImport(ids, headerEnhanced);
    }

    private async void ApplyParsedImport(List<string> ids, bool? headerEnhanced)
    {
        if (ids.Count == 0) return;

        var global = _enhanced.Available.Concat(_enhanced.Ordered)
            .Concat(_legacy.Available).Concat(_legacy.Ordered)
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // «нет мода» = отсутствует в списках ИЛИ есть как плейсхолдер «не скачан»
        bool Have(string id) => global.TryGetValue(id, out var m) && m.Downloaded;
        var missingIds = ids.Where(id => !Have(id)).ToList();

        // данные тянем только для НОВЫХ (которых ещё нет в списках)
        var toFetch = missingIds.Where(id => !global.ContainsKey(id)).ToList();
        var missDetails = new Dictionary<string, SteamWorkshopApi.Details>();
        if (toFetch.Count > 0)
        {
            try { missDetails = await SteamWorkshopApi.GetDetailsAsync(toFetch); } catch { }
        }

        bool DefaultEnh() => headerEnhanced ?? ReferenceEquals(_current, _enhanced);
        bool VersionOf(string id) =>
            global.TryGetValue(id, out var m) ? m.IsEnhanced
            : missDetails.TryGetValue(id, out var d) ? d.IsEnhanced
            : DefaultEnh();

        // плейсхолдеры «не скачан»: существующие (из modlist) переиспользуем, новые создаём
        var placeholders = new List<ModItem>();
        foreach (var id in missingIds)
        {
            if (global.TryGetValue(id, out var existing))
            {
                placeholders.Add(existing);
                continue;
            }
            bool enh = VersionOf(id);
            var ph = new ModItem(id, "", downloaded: false) { IsEnhanced = enh };
            if (missDetails.TryGetValue(id, out var d))
            {
                if (!string.IsNullOrWhiteSpace(d.Title)) ph.Title = d.Title;
                ph.PreviewUrl = d.PreviewUrl;
                ph.FileSize = d.FileSize;
            }
            (enh ? _enhanced : _legacy).Available.Add(ph);
            placeholders.Add(ph);
        }

        // раскладываем по версии (плейсхолдеры уже в списках, попадут в порядок)
        var enhIds = ids.Where(VersionOf).ToList();
        var legIds = ids.Where(id => !VersionOf(id)).ToList();

        bool endEnhanced = headerEnhanced ?? enhIds.Count >= legIds.Count;
        if (endEnhanced)
        {
            if (legIds.Count > 0) ApplyOrderToVersion(false, legIds);
            if (enhIds.Count > 0) ApplyOrderToVersion(true, enhIds);
        }
        else
        {
            if (enhIds.Count > 0) ApplyOrderToVersion(true, enhIds);
            if (legIds.Count > 0) ApplyOrderToVersion(false, legIds);
        }

        if (placeholders.Count > 0)
        {
            _ = LoadCachedThumbnailsAsync(placeholders);
            _ = DownloadThumbnailsAsync(placeholders);
        }

        RefreshPresetDirty();

        int total = ids.Count, missing = missingIds.Count, ready = total - missing;
        void Redownload() => DownloadMissing(missingIds, onComplete: () => ApplyParsedImport(ids, headerEnhanced));

        if (missing > 0)
        {
            txtStatus.Text = $"Импортировано {total} модов: {ready} готовы, {missing} не скачано. " +
                             "Нажмите «Скачать», затем «Применить».";
            ShowToast(
                $"Импортировано {total}: {ready} готовы, {missing} не скачано.",
                ToastKind.Info,
                actionText: $"Скачать ({missing})",
                action: Redownload, seconds: 10);
        }
        else
        {
            txtStatus.Text = $"Импортировано {total} модов. Нажмите «Применить», чтобы записать в modlist.txt.";
            ShowToast($"Импортировано {total} модов. Нажмите «Применить».", ToastKind.Success);
        }
    }

    private void OpenModPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModItem m && long.TryParse(m.ModId, out _))
            TryOpen($"https://steamcommunity.com/sharedfiles/filedetails/?id={m.ModId}");
        else
            ShowToast("У этого мода нет Workshop-ID (локальный мод).", ToastKind.Warning);
    }

    private void OpenModFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModItem m) return;

        if (File.Exists(m.PakPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{m.PakPath}\"")
            {
                UseShellExecute = true
            });
            return;
        }

        var dir = Path.GetDirectoryName(m.PakPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            TryOpen(dir);
        else
            ShowToast("Папка мода не найдена на диске.", ToastKind.Warning);
    }

    private void CopyModId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModItem m) return;
        try
        {
            Clipboard.SetText(m.ModId);
            ShowToast($"ID скопирован: {m.ModId}", ToastKind.Success, seconds: 2);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось скопировать ID: {ex.Message}", ToastKind.Warning);
        }
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModItem m) return;

        string? folder = null;
        if (!string.IsNullOrEmpty(_settings.ModsFolder))
        {
            var byId = Path.Combine(_settings.ModsFolder, m.ModId);
            if (Directory.Exists(byId)) folder = byId;
        }
        folder ??= Path.GetDirectoryName(m.PakPath);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            ShowToast("Папка мода не найдена на диске.", ToastKind.Warning);
            return;
        }

        if (MessageDialog.Show(this, "Удаление мода", $"Удалить мод «{m.Title}» с диска?\n\n{folder}",
                MessageButtons.YesNo, MessageKind.Warning) != MessageResult.Yes)
            return;

        try
        {
            Directory.Delete(folder, recursive: true);
            RemoveFromLists(m);
            UpdateStatus();
            ShowToast($"Мод «{m.Title}» удалён с диска.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось удалить: {ex.Message}", ToastKind.Warning, seconds: 7);
        }
    }

    private void RemoveFromLists(ModItem m)
    {
        _enhanced.Available.Remove(m);
        _enhanced.Ordered.Remove(m);
        _legacy.Available.Remove(m);
        _legacy.Ordered.Remove(m);
    }

    private static bool TryOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Скачивание модов (SteamKit2, напрямую с CDN Valve) ----------

    private SteamDownloader? _steam;
    private SteamDownloader Steam => _steam ??= new SteamDownloader();

    // ---------- Вход в Steam и подписка (для игры на серверах) ----------

    private SteamAccount? _account;
    private SteamAccount Account => _account ??= new SteamAccount();

    private void UpdateSteamUi() =>
        btnSteamLogout.Visibility = _settings.HasSteamLogin ? Visibility.Visible : Visibility.Collapsed;

    // Гарантирует вход: сначала по сохранённому токену, иначе — диалог логина/пароля с 2FA.
    private async Task<bool> EnsureSteamLoginAsync()
    {
        if (Account.IsLoggedOn) return true;

        // 1) сохранённый refresh-токен
        if (_settings.HasSteamLogin)
        {
            var token = _settings.GetSteamRefreshToken();
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    busyText.Text = "Вхожу в Steam…";
                    await Account.LoginWithTokenAsync(_settings.SteamAccountName!, token!, CancellationToken.None);
                    return true;
                }
                catch { /* токен истёк — попросим пароль */ }
            }
        }

        // 2) QR-код (по умолчанию) или логин/пароль — через диалог входа
        HideBusy();
        var dlg = new QrLoginDialog(Account) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.LoginResult is { } result)
        {
            _settings.SaveSteamLogin(result.AccountName, result.RefreshToken);
            UpdateSteamUi();
            return true;
        }
        return false;
    }

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        var ids = AllMods()
            .Where(m => ulong.TryParse(m.ModId, out _))
            .Select(m => ulong.Parse(m.ModId))
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            ShowToast("В списке нет модов для подписки.", ToastKind.Warning);
            return;
        }

        ShowBusy("Подключаюсь к Steam…");
        bool logged = await EnsureSteamLoginAsync();
        if (!logged) { HideBusy(); return; }

        int ok;
        try
        {
            ShowBusy($"Подписка: 0/{ids.Count}…");
            var progress = new Progress<(int done, int total)>(p =>
                busyText.Text = $"Подписка: {p.done}/{p.total}…");
            ok = await Account.SubscribeAsync(ids, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            HideBusy();
            ShowToast($"Ошибка подписки: {ex.Message}", ToastKind.Warning, seconds: 8);
            return;
        }
        finally
        {
            HideBusy();
        }

        ShowToast($"Подписка оформлена: {ok} из {ids.Count}. Запустите Steam — он синхронизирует и " +
                  "докачает моды (уже скачанные подхватятся из папки).", ToastKind.Success, seconds: 12);
    }

    private void SteamLogout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageDialog.Show(this, "Выход из Steam", "Забыть сохранённый вход в Steam?",
                MessageButtons.YesNo, MessageKind.Question) != MessageResult.Yes)
            return;

        _account?.Dispose();
        _account = null;
        _settings.ClearSteamLogin();
        UpdateSteamUi();
        ShowToast("Вход в Steam забыт.", ToastKind.Info, seconds: 4);
    }

    private string TitleFor(string id) =>
        AllMods().FirstOrDefault(m => string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase))?.Title ?? id;

    private async void DownloadMissing(List<string> ids, Action? onComplete = null)
    {
        if (ids.Count == 0) return;

        var modsFolder = _settings.ModsFolder;
        if (string.IsNullOrEmpty(modsFolder))
        {
            ShowToast("Не задана папка модов.", ToastKind.Warning);
            return;
        }

        var toGet = ids.Where(id => ulong.TryParse(id, out _)).ToList();
        if (toGet.Count == 0)
        {
            ShowToast("Нет корректных Workshop-ID для загрузки.", ToastKind.Warning);
            return;
        }

        ShowBusy("Подключаюсь к Steam…");
        try
        {
            await Steam.EnsureLoggedInAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            HideBusy();
            ShowToast($"Не удалось подключиться к Steam: {ex.Message}", ToastKind.Warning, seconds: 7);
            return;
        }

        var failed = new List<string>();
        int ok = 0;
        try
        {
            for (int i = 0; i < toGet.Count; i++)
            {
                var id = toGet[i];
                int n = i + 1;
                var dst = Path.Combine(modsFolder, id);
                busyText.Text = $"Скачиваю {n}/{toGet.Count}: {TitleFor(id)}…";
                var progress = new Progress<double>(p =>
                    busyText.Text = $"Скачиваю {n}/{toGet.Count}: {TitleFor(id)} — {p:P0}");
                try
                {
                    await Steam.DownloadWorkshopItemAsync(ulong.Parse(id), dst, progress, CancellationToken.None);
                    ok++;
                }
                catch (Exception)
                {
                    failed.Add(id);
                }
            }
        }
        finally
        {
            HideBusy();
        }

        await ReloadAsync();

        if (onComplete != null)
        {
            onComplete();
        }
        else if (failed.Count == 0)
        {
            ShowToast($"Готово — скачано {ok} модов.", ToastKind.Success);
        }
        else
        {
            ShowToast($"Скачано {ok}, не удалось {failed.Count}.", ToastKind.Warning,
                actionText: "Повторить", action: () => DownloadMissing(failed));
        }
    }

    private string? DeriveLibraryRoot()
    {
        var f = _settings.ModsFolder;
        if (string.IsNullOrEmpty(f)) return null;
        var idx = f.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? f.Substring(0, idx) : null;
    }

    private static (List<string> ids, bool? enhanced) ParseSharedIds(string? text)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? enhanced = null;
        if (string.IsNullOrWhiteSpace(text)) return (ids, enhanced);

        foreach (var raw in text.Replace("\r", "").Split('\n', ','))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                if (line.Contains("Enhanced", StringComparison.OrdinalIgnoreCase)) enhanced = true;
                else if (line.Contains("Legacy", StringComparison.OrdinalIgnoreCase)) enhanced = false;
                continue;
            }

            string? id = null;
            var mPath = Regex.Match(line, @"[\\/]440900[\\/](\d+)");
            if (mPath.Success) id = mPath.Groups[1].Value;

            if (id == null)
            {
                var mUrl = Regex.Match(line, @"[?&]id=(\d+)", RegexOptions.IgnoreCase);
                if (mUrl.Success) id = mUrl.Groups[1].Value;
            }
            if (id == null)
            {
                var mBare = Regex.Match(line, @"^\*?(\d{4,})\b");
                if (mBare.Success) id = mBare.Groups[1].Value;
            }

            if (id != null && seen.Add(id))
                ids.Add(id);
        }

        return (ids, enhanced);
    }

    private enum ToastKind { Success, Info, Warning }

    private DispatcherTimer? _toastTimer;
    private Action? _toastAction;

    private void ShowToast(string message, ToastKind kind = ToastKind.Success,
                           string? actionText = null, Action? action = null, int seconds = 4)
    {
        toastText.Text = message;

        var (color, glyph) = kind switch
        {
            ToastKind.Warning => (Color.FromRgb(0xE0, 0xA8, 0x30), "!"),
            ToastKind.Info    => (Color.FromRgb(0x3A, 0x7B, 0xD5), "i"),
            _                 => (Color.FromRgb(0x46, 0xB4, 0x5A), "✓"),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        toastBar.Background = brush;
        toastIcon.Foreground = brush;
        toastIcon.Text = glyph;

        _toastAction = action;
        bool hasAction = !string.IsNullOrEmpty(actionText) && action != null;
        if (hasAction)
        {
            toastAction.Content = actionText;
            toastAction.Visibility = Visibility.Visible;
        }
        else
        {
            toastAction.Visibility = Visibility.Collapsed;
        }

        toast.Visibility = Visibility.Visible;
        toast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        toastShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _toastTimer ??= new DispatcherTimer();
        _toastTimer.Stop();
        if (!hasAction)
        {
            _toastTimer.Tick -= ToastTimer_Tick;
            _toastTimer.Tick += ToastTimer_Tick;
            _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
            _toastTimer.Start();
        }
    }

    private void ToastTimer_Tick(object? sender, EventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer?.Stop();
        var fade = new DoubleAnimation(toast.Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => { if (toast.Opacity == 0) toast.Visibility = Visibility.Collapsed; };
        toast.BeginAnimation(OpacityProperty, fade);
        toastShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(160)));
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

    private void ShowBusy(string text)
    {
        busyText.Text = text;
        busyOverlay.Visibility = Visibility.Visible;
        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        spin.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
    }

    private void HideBusy()
    {
        spin.BeginAnimation(RotateTransform.AngleProperty, null);
        busyOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        var name = ReferenceEquals(_current, _enhanced) ? "Enhanced" : "Legacy";
        txtStatus.Text = $"{name}: доступно {_current.Available.Count}, в порядке {_current.Ordered.Count}.";
    }

    private async Task InitializeAsync()
    {
        var modsFolder = _settings.ModsFolder;
        if (string.IsNullOrEmpty(modsFolder) || !Directory.Exists(modsFolder))
            modsFolder = SteamLocator.FindModsFolder();
        if (!string.IsNullOrEmpty(modsFolder))
            _settings.ModsFolder = modsFolder;
        txtModsFolder.Text = modsFolder ?? "(не найдена — выберите вручную)";

        var modlist = _settings.ModlistPath;
        if (string.IsNullOrEmpty(modlist))
            modlist = SteamLocator.FindDefaultModlistPath();
        if (!string.IsNullOrEmpty(modlist))
            _settings.ModlistPath = modlist;
        txtModlist.Text = modlist ?? "(не указан — выберите вручную)";

        if (string.IsNullOrEmpty(_settings.GamePath) || !File.Exists(_settings.GamePath))
        {
            var exe = SteamLocator.FindGameExe();
            if (!string.IsNullOrEmpty(exe)) _settings.GamePath = exe;
        }

        _settings.Save();
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _enhanced.Available.Clear();
        _enhanced.Ordered.Clear();
        _legacy.Available.Clear();
        _legacy.Ordered.Clear();

        var folder = _settings.ModsFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            txtStatus.Text = "Папка модов не задана.";
            return;
        }

        txtStatus.Text = "Сканирую папку модов…";
        ShowBusy("Сканирую папку модов…");
        try
        {
            var byId = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                var modId = Path.GetFileName(dir);
                var pak = Directory
                    .EnumerateFiles(dir, "*.pak", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (pak == null) continue;
                byId[modId] = new ModItem(modId, pak);
            }

            // моды из modlist.txt, которых нет в папке — «не скачан» (плейсхолдеры)
            var orderedIds = ReadModlistOrder(_settings.ModlistPath);
            var orphanIds = orderedIds
                .Where(x => x.id != null && !byId.ContainsKey(x.id!))
                .Select(x => x.id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var orphans = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in orphanIds)
                orphans[id] = new ModItem(id, "", downloaded: false);

            _ = LoadCachedThumbnailsAsync(byId.Values.Concat(orphans.Values).ToList());

            txtStatus.Text = $"Модов: {byId.Count}. Загружаю данные из Steam…";
            busyText.Text = $"Загружаю данные о {byId.Count} модах из Steam…";

            string? error = null;
            try
            {
                var details = await SteamWorkshopApi.GetDetailsAsync(byId.Keys.Concat(orphanIds));

                var libRoot = DeriveLibraryRoot();
                var installed = libRoot != null
                    ? SteamLocator.ReadInstalledWorkshopTimes(libRoot)
                    : new Dictionary<string, long>();

                foreach (var item in byId.Values)
                {
                    if (details.TryGetValue(item.ModId, out var d))
                    {
                        if (!string.IsNullOrWhiteSpace(d.Title)) item.Title = d.Title!;
                        item.FileSize = d.FileSize;
                        item.PreviewUrl = d.PreviewUrl;
                        item.IsEnhanced = d.IsEnhanced;

                        if (d.TimeUpdated > 0)
                        {
                            long local = installed.TryGetValue(item.ModId, out var t) && t > 0
                                ? t
                                : FolderUnixTime(Path.Combine(folder, item.ModId));
                            item.NeedsUpdate = local > 0 && d.TimeUpdated > local;
                        }
                    }
                }

                foreach (var ph in orphans.Values)
                {
                    if (details.TryGetValue(ph.ModId, out var d))
                    {
                        if (!string.IsNullOrWhiteSpace(d.Title)) ph.Title = d.Title!;
                        ph.IsEnhanced = d.IsEnhanced;
                        ph.PreviewUrl = d.PreviewUrl;
                        ph.FileSize = d.FileSize;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Partition(byId, orphans, orderedIds);

            if (error == null)
                _ = DownloadThumbnailsAsync(byId.Values.Concat(orphans.Values).ToList());

            UpdateStatus();
            int updates = AllMods().Count(m => m.NeedsUpdate);
            if (updates > 0)
                txtStatus.Text += $"  ·  обновлений: {updates}";
            if (error != null)
                txtStatus.Text += $"  (Steam недоступен: {error} — версии не определены, всё в Legacy)";

            RefreshPresetDirty();
            SetModlistBaseline();
        }
        finally
        {
            HideBusy();
        }
    }

    private void Partition(Dictionary<string, ModItem> byId,
        Dictionary<string, ModItem> orphans, List<(string? id, string? path)> orderedIds)
    {
        VersionLists ListsFor(ModItem m) => m.IsEnhanced ? _enhanced : _legacy;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, path) in orderedIds)
        {
            ModItem? item =
                id != null && byId.TryGetValue(id, out var real) ? real
                : id != null && orphans.TryGetValue(id, out var ph) ? ph
                : path != null
                    ? new ModItem(Path.GetFileNameWithoutExtension(path), path, downloaded: File.Exists(path))
                        { Title = Path.GetFileNameWithoutExtension(path) }
                    : null;

            if (item != null && used.Add(item.ModId))
                ListsFor(item).Ordered.Add(item);
        }

        foreach (var item in byId.Values
                     .Where(m => !used.Contains(m.ModId))
                     .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
        {
            ListsFor(item).Available.Add(item);
        }
    }

    private static Task LoadCachedThumbnailsAsync(List<ModItem> items) => Task.Run(() =>
    {
        foreach (var item in items)
        {
            if (item.Thumb != null) continue;
            var img = ImageCache.LoadCached(item.ModId);
            if (img != null) item.Thumb = img;
        }
    });

    private static Task DownloadThumbnailsAsync(List<ModItem> items) => Task.Run(async () =>
    {
        foreach (var item in items)
        {
            if (item.Thumb != null || string.IsNullOrEmpty(item.PreviewUrl)) continue;
            var img = await ImageCache.DownloadAsync(item.ModId, item.PreviewUrl).ConfigureAwait(false);
            if (img != null) item.Thumb = img;
            await Task.Delay(25).ConfigureAwait(false);
        }
    });

    private void ResortAvailable()
    {
        var coll = _current.Available;
        var sorted = coll.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
        coll.Clear();
        foreach (var m in sorted) coll.Add(m);
    }

    private static List<(string? id, string? path)> ReadModlistOrder(string? modlistPath)
    {
        var result = new List<(string?, string?)>();
        if (string.IsNullOrEmpty(modlistPath) || !File.Exists(modlistPath))
            return result;

        foreach (var raw in File.ReadAllLines(modlistPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = Regex.Match(line, @"[\\/]440900[\\/](\d+)[\\/]", RegexOptions.IgnoreCase);
            var id = m.Success ? m.Groups[1].Value : null;
            result.Add((id, line));
        }
        return result;
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragSource = sender as ListBox;
        _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as ModItem;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _dragSource == null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = _dragSource;
        var item = _dragItem;
        _dragItem = null;
        _dragSource = null;

        var container = source.ItemContainerGenerator.ContainerFromItem(item) as UIElement;
        if (container != null)
        {
            _adornerLayer = AdornerLayer.GetAdornerLayer(RootGrid);
            _adorner = new DragAdorner(RootGrid, container);
            _adornerLayer?.Add(_adorner);
            UpdateAdornerToCursor();
            GiveFeedback += OnGiveFeedback;
        }

        try
        {
            DragDrop.DoDragDrop(source, item, DragDropEffects.Move);
        }
        finally
        {
            GiveFeedback -= OnGiveFeedback;
            if (_adorner != null) _adornerLayer?.Remove(_adorner);
            _adorner = null;
            _adornerLayer = null;
            RemoveInsertion();
        }
    }

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        UpdateAdornerToCursor();
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void UpdateAdornerToCursor()
    {
        if (_adorner == null) return;
        if (!GetCursorPos(out var p)) return;
        var local = RootGrid.PointFromScreen(new Point(p.X, p.Y));
        _adorner.SetPosition(local.X, local.Y);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(ModItem));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        UpdateAdornerToCursor();

        if (ok && sender is ListBox lb)
            ShowInsertion(lb, GetInsertIndex(lb, e.GetPosition(lb)));

        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _insertionList))
            RemoveInsertion();
    }

    private void ShowInsertion(ListBox lb, int index)
    {
        if (!ReferenceEquals(_insertionList, lb))
        {
            RemoveInsertion();
            _insertion = new InsertionAdorner(lb);
            AdornerLayer.GetAdornerLayer(lb)?.Add(_insertion);
            _insertionList = lb;
        }
        _insertion?.SetY(InsertionY(lb, index));
    }

    private void RemoveInsertion()
    {
        if (_insertion != null && _insertionList != null)
            AdornerLayer.GetAdornerLayer(_insertionList)?.Remove(_insertion);
        _insertion = null;
        _insertionList = null;
    }

    private static double InsertionY(ListBox lb, int index)
    {
        if (lb.Items.Count == 0) return 6;

        if (index < lb.Items.Count)
        {
            if (lb.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem c)
                return c.TranslatePoint(new Point(0, 0), lb).Y;
        }
        if (lb.ItemContainerGenerator.ContainerFromIndex(lb.Items.Count - 1) is ListBoxItem last)
            return last.TranslatePoint(new Point(0, 0), lb).Y + last.ActualHeight;

        return 6;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox target) return;
        if (e.Data.GetData(typeof(ModItem)) is not ModItem item) return;

        var source = SourceOf(item);
        if (source == null) return;

        var targetColl = CollectionOf(target);
        if (targetColl == null) return;

        int insertIndex = GetInsertIndex(target, e.GetPosition(target));
        int oldIndex = source.IndexOf(item);

        source.Remove(item);
        if (ReferenceEquals(source, targetColl) && oldIndex >= 0 && oldIndex < insertIndex)
            insertIndex--;

        insertIndex = Math.Clamp(insertIndex, 0, targetColl.Count);
        targetColl.Insert(insertIndex, item);
        UpdateStatus();
        RefreshPresetDirty();
    }

    private ObservableCollection<ModItem>? SourceOf(ModItem item) =>
        _current.Available.Contains(item) ? _current.Available
        : _current.Ordered.Contains(item) ? _current.Ordered : null;

    private ObservableCollection<ModItem>? CollectionOf(ListBox lb) =>
        ReferenceEquals(lb, lstAvailable) ? _current.Available
        : ReferenceEquals(lb, lstOrdered) ? _current.Ordered : null;

    private static int GetInsertIndex(ListBox lb, Point pos)
    {
        for (int i = 0; i < lb.Items.Count; i++)
        {
            if (lb.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), lb);
            if (pos.Y < top.Y + container.ActualHeight / 2)
                return i;
        }
        return lb.Items.Count;
    }

    private void Move(ModItem item, ObservableCollection<ModItem> from, ObservableCollection<ModItem> to, int index = -1)
    {
        from.Remove(item);
        if (index < 0 || index > to.Count) index = to.Count;
        to.Insert(index, item);
    }

    private void MoveRight_Click(object sender, RoutedEventArgs e)
    {
        if (lstAvailable.SelectedItem is ModItem item)
        {
            Move(item, _current.Available, _current.Ordered);
            lstOrdered.SelectedItem = item;
            UpdateStatus();
            RefreshPresetDirty();
        }
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e)
    {
        if (lstOrdered.SelectedItem is ModItem item)
        {
            Move(item, _current.Ordered, _current.Available);
            ResortAvailable();
            lstAvailable.SelectedItem = item;
            UpdateStatus();
            RefreshPresetDirty();
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int i = lstOrdered.SelectedIndex;
        if (i > 0)
        {
            _current.Ordered.Move(i, i - 1);
            lstOrdered.SelectedIndex = i - 1;
            RefreshPresetDirty();
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int i = lstOrdered.SelectedIndex;
        if (i >= 0 && i < _current.Ordered.Count - 1)
        {
            _current.Ordered.Move(i, i + 1);
            lstOrdered.SelectedIndex = i + 1;
            RefreshPresetDirty();
        }
    }

    private void Available_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ModItem item)
        {
            Move(item, _current.Available, _current.Ordered);
            UpdateStatus();
            RefreshPresetDirty();
        }
    }

    private void Ordered_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ModItem item)
        {
            Move(item, _current.Ordered, _current.Available);
            ResortAvailable();
            UpdateStatus();
            RefreshPresetDirty();
        }
    }

    private void ChooseModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Папка с модами (…/workshop/content/440900)" };
        if (!string.IsNullOrEmpty(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
            dlg.InitialDirectory = _settings.ModsFolder;
        if (dlg.ShowDialog() == true)
        {
            _settings.ModsFolder = dlg.FolderName;
            txtModsFolder.Text = dlg.FolderName;
            _settings.Save();
            _ = ReloadAsync();
        }
    }

    private void ChooseModlist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Файл modlist.txt",
            Filter = "modlist (*.txt)|*.txt|Все файлы|*.*",
            CheckFileExists = false
        };
        if (dlg.ShowDialog() == true)
        {
            _settings.ModlistPath = dlg.FileName;
            txtModlist.Text = dlg.FileName;
            _settings.Save();
            _ = ReloadAsync();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private IEnumerable<ModItem> AllMods() =>
        _enhanced.Available.Concat(_enhanced.Ordered)
            .Concat(_legacy.Available).Concat(_legacy.Ordered);

    private void UpdateMods_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.ModsFolder))
        {
            ShowToast("Папка модов не определена.", ToastKind.Warning);
            return;
        }

        var toGet = AllMods().Where(m => m.NeedsUpdate || m.NotDownloaded)
            .Select(m => m.ModId)
            .Where(id => long.TryParse(id, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (toGet.Count == 0)
        {
            ShowToast("Всё на месте — обновлять и докачивать нечего.", ToastKind.Success);
            return;
        }

        ShowToast($"Скачиваю {toGet.Count} мод(ов) (обновления и недостающие)…", ToastKind.Info, seconds: 5);
        DownloadMissing(toGet);
    }

    private static long FolderUnixTime(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            DateTime newest = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newest) newest = t;
            }
            return newest == DateTime.MinValue ? 0 : ((DateTimeOffset)newest).ToUnixTimeSeconds();
        }
        catch { return 0; }
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        var exe = _settings.GamePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            exe = SteamLocator.FindGameExe();

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            var dlg = new OpenFileDialog
            {
                Title = "Укажите ConanSandbox-Win64-Shipping.exe",
                Filter = "Conan Exiles (*.exe)|*.exe|Все файлы|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;
            exe = dlg.FileName;
        }

        _settings.GamePath = exe;
        _settings.Save();

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
            txtStatus.Text = "Игра запускается…";
            ShowToast("Conan Exiles запускается…", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось запустить игру: {ex.Message}", ToastKind.Warning, seconds: 7);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => WriteModlist();

    private bool WriteModlist()
    {
        var modlist = _settings.ModlistPath;
        if (string.IsNullOrEmpty(modlist))
        {
            ShowToast("Сначала укажите путь к modlist.txt.", ToastKind.Warning);
            return false;
        }

        var version = ReferenceEquals(_current, _enhanced) ? "Enhanced" : "Legacy";
        try
        {
            var dir = Path.GetDirectoryName(modlist);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // нескачанные (плейсхолдеры) пропускаем — у них нет пути к .pak
            var lines = _current.Ordered.Where(m => m.Downloaded).Select(m => m.PakPath).ToArray();
            int skipped = _current.Ordered.Count - lines.Length;
            File.WriteAllLines(modlist, lines);

            _settings.Save();
            SetModlistBaseline();
            var skipNote = skipped > 0 ? $" (пропущено {skipped} не скачанных)" : "";
            txtStatus.Text = $"[{version}] записано {lines.Length} модов в {modlist}{skipNote}";
            ShowToast($"Готово! {version}: записано {lines.Length} модов в modlist.txt{skipNote}", ToastKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось записать modlist.txt: {ex.Message}", ToastKind.Warning, seconds: 7);
            return false;
        }
    }
}
