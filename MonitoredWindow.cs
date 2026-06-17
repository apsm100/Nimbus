using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace VmMonitor;

/// <summary>
/// Represents a single VM window being monitored and its latest captured state.
/// Bound directly to the dashboard UI.
/// </summary>
public sealed class MonitoredWindow : INotifyPropertyChanged
{
    public IntPtr Handle { get; }

    public MonitoredWindow(IntPtr handle, string title, string processName)
    {
        Handle = handle;
        _title = title;
        _processName = processName;
    }

    private string _title;
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private string _customName = "";
    /// <summary>User-assigned chat title that overrides the OCR-read one when set.</summary>
    public string CustomName
    {
        get => _customName;
        set
        {
            if (Set(ref _customName, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(HasCustomName));
            }
        }
    }

    /// <summary>True when the user has assigned a custom title (drives the blue pencil).</summary>
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_customName);

    /// <summary>The chat title shown on the card: custom name if given, else the OCR title, else UNTITLED — upper-cased.</summary>
    public string DisplayTitle
    {
        get
        {
            string name = !string.IsNullOrWhiteSpace(_customName) ? _customName
                        : !string.IsNullOrWhiteSpace(_focus) ? _focus
                        : "Untitled";
            return name.ToUpperInvariant();
        }
    }

    private bool _isEditing;
    /// <summary>True while the user is renaming this card inline.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    private string _processName;
    public string ProcessName
    {
        get => _processName;
        set => Set(ref _processName, value);
    }

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    private string _status = "Pending";
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(StatusDescription));
        }
    }

    /// <summary>Human-readable explanation of the current status, shown on hover.</summary>
    public string StatusDescription => _status switch
    {
        "Active" => "Active — chat content changed since the last check",
        "Idle" => "Idle — no change since the last check",
        "Captured" => "Captured — baseline recorded; now watching for changes",
        "Viewing" => "Viewing — you're currently focused on this VM",
        "Minimized" => "Minimized — window is minimized, can't read it",
        _ => _status
    };

    private string _focus = "";
    public string Focus
    {
        get => _focus;
        set
        {
            string old = _focus;
            if (Set(ref _focus, value))
            {
                // Remember the previous derived title so the card can show recent history.
                if (!string.IsNullOrWhiteSpace(old) && !TitlesEqual(old, value))
                    _titleHistory.Insert(0, old);

                // Never show a history line that matches the current title (OCR can
                // flip back and forth) or an adjacent duplicate.
                _titleHistory.RemoveAll(t => string.IsNullOrWhiteSpace(t) || TitlesEqual(t, value));
                for (int i = _titleHistory.Count - 1; i > 0; i--)
                    if (TitlesEqual(_titleHistory[i], _titleHistory[i - 1]))
                        _titleHistory.RemoveAt(i);
                if (_titleHistory.Count > 2)
                    _titleHistory.RemoveRange(2, _titleHistory.Count - 2);

                OnPropertyChanged(nameof(PreviousTitle1));
                OnPropertyChanged(nameof(PreviousTitle2));
                OnPropertyChanged(nameof(HasPreviousTitle1));
                OnPropertyChanged(nameof(HasPreviousTitle2));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private readonly List<string> _titleHistory = new();

    private static bool TitlesEqual(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Most recent previous derived chat title (upper-cased).</summary>
    public string PreviousTitle1 => _titleHistory.Count > 0 ? _titleHistory[0].ToUpperInvariant() : "";
    /// <summary>Second-most-recent previous derived chat title (upper-cased).</summary>
    public string PreviousTitle2 => _titleHistory.Count > 1 ? _titleHistory[1].ToUpperInvariant() : "";
    public bool HasPreviousTitle1 => _titleHistory.Count > 0;
    public bool HasPreviousTitle2 => _titleHistory.Count > 1;

    private string _fullText = "";
    public string FullText
    {
        get => _fullText;
        set => Set(ref _fullText, value);
    }

    private DateTime _lastUpdated;
    public DateTime LastUpdated
    {
        get => _lastUpdated;
        set
        {
            if (Set(ref _lastUpdated, value))
                OnPropertyChanged(nameof(LastUpdatedText));
        }
    }

    public string LastUpdatedText =>
        _lastUpdated == default ? "never" : _lastUpdated.ToString("HH:mm:ss");

    /// <summary>Hash of the previous chat-body text, used for change detection.</summary>
    public long PreviousTextHash { get; set; }

    /// <summary>Monotonic ordering key; higher means more recently opened/activated.</summary>
    public long Order { get; set; }

    private bool _isPinned;
    /// <summary>When true, the card is kept at the top of the list above unpinned ones.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
