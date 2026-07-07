using System.ComponentModel;
using System.Windows.Media;

namespace ConanModsSort;

public class ModItem : INotifyPropertyChanged
{
    public string ModId { get; }
    public string PakPath { get; }

    private string _title;
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Display)); }
    }

    private long _fileSize;
    public long FileSize
    {
        get => _fileSize;
        set { _fileSize = value; OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(Display)); }
    }

    public string? PreviewUrl { get; set; }

    public bool IsEnhanced { get; set; }

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(nameof(Thumb)); }
    }

    private bool _needsUpdate;
    public bool NeedsUpdate
    {
        get => _needsUpdate;
        set { _needsUpdate = value; OnPropertyChanged(nameof(NeedsUpdate)); }
    }

    public ModItem(string modId, string pakPath)
    {
        ModId = modId;
        PakPath = pakPath;
        _title = modId;
    }

    public string Display => Title;

    public string SizeText => FileSize > 0
        ? $"{FileSize / (1024.0 * 1024.0):0.0} МБ"
        : "";

    public string SubText => string.IsNullOrEmpty(SizeText) ? ModId : $"{ModId} · {SizeText}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
