using System.IO;
using System.Text.Json;

namespace VmMonitor;

/// <summary>
/// Remembers "what was last done with" each monitored window — the last activity title
/// we read plus the user's customisations (name, pin, idle) — keyed by the window's
/// title, which uniquely identifies each VM (e.g. "amritmanhas-3"). Persisted as JSON
/// under %LocalAppData%\Nimbus\memory.json so the dashboard can restore that context
/// after a restart or when a window reappears.
/// </summary>
public static class WindowMemory
{
    /// <summary>One remembered record per window-title key.</summary>
    public sealed class Entry
    {
        /// <summary>The most recent activity/chat title we derived for the window.</summary>
        public string LastTitle { get; set; } = "";
        /// <summary>Earlier titles, newest first, so the card's history survives restarts.</summary>
        public List<string> History { get; set; } = new();
        /// <summary>User-assigned card name, if any.</summary>
        public string CustomName { get; set; } = "";
        public bool IsPinned { get; set; }
        public bool IsIdle { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nimbus", "memory.json");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = Load();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
                if (data is not null)
                    return new Dictionary<string, Entry>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* corrupt or unreadable store — start fresh */ }
        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Entries, WriteOptions));
        }
        catch { /* best-effort persistence */ }
    }

    /// <summary>Returns the remembered record for a window title, or null if none exists.</summary>
    public static Entry? Get(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        lock (Gate)
            return Entries.TryGetValue(title, out var e) ? e : null;
    }

    /// <summary>Records the latest state of a window under its title key.</summary>
    public static void Remember(MonitoredWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Title)) return;
        lock (Gate)
        {
            Entries[window.Title] = new Entry
            {
                LastTitle = window.Focus ?? "",
                History = window.TitleHistory.ToList(),
                CustomName = window.CustomName ?? "",
                IsPinned = window.IsPinned,
                IsIdle = window.IsIdle,
                LastSeen = DateTime.Now
            };
            Save();
        }
    }

    /// <summary>Wipes all remembered state, both in memory and on disk.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* ignore */ }
        }
    }
}
