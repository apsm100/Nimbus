using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace VmMonitor;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MonitoredWindow> _windows = new();
    private readonly Dictionary<IntPtr, MonitoredWindow> _byHandle = new();
    private readonly Dictionary<IntPtr, byte[]> _signatures = new();
    // Last-known signature of each window's header band, so the periodic title pass
    // can skip the expensive OCR call when the title hasn't visibly changed.
    private readonly Dictionary<IntPtr, byte[]> _titleSignatures = new();
    // Windows the user manually focused since the last status scan. Their pixels
    // changed because of the user, not the VM, so the next scan rebaselines them
    // silently instead of flagging a (false) green "Active".
    private readonly HashSet<IntPtr> _viewedSinceScan = new();
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _updateTimer = new();
    private WinForms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;
    private bool _trayActive;
    private NativeMethods.WinEventDelegate? _winEventProc;
    private IntPtr _winEventHook;
    private IntPtr _foregroundHandle;
    private bool _isPolling = true;
    private bool _refreshInProgress;
    private bool _updateInProgress;
    private bool _exiting;
    private bool _suppressNextSelection;
    private long _orderSeq;
    private MonitoredWindow? _viewingItem;
    private MonitoredWindow? _currentItem;

    public MainWindow()
    {
        InitializeComponent();
        WindowsList.ItemsSource = _windows;
        SetupTray();
        SetupForegroundHook();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        FooterText.Text = $"Nimbus v{ver?.ToString(3) ?? "1.0.0"}  ·  made with \u2665 by apsm";
        _timer.Tick += async (_, _) => await RefreshAsync();
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        Loaded += async (_, _) =>
        {
            PositionToTray();
            ApplyInterval();
            StartupCheck.IsChecked = IsStartupEnabled();
            _timer.Start();
            _updateTimer.Start();
            await RefreshAsync();
            InitOverlay.Visibility = Visibility.Collapsed;
            PositionToTray();
            // Reclaim the memory spike from JIT + first OCR load once startup settles.
            TrimWorkingSet();
        };
        // Re-anchor to the tray corner whenever the content height changes,
        // so a growing list never spills under the taskbar.
        SizeChanged += (_, _) => PositionToTray();

        // Keep the acrylic backdrop's dark/light tint in sync with the system theme.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General)
                Dispatcher.Invoke(ApplyBackdrop);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    /// <summary>Applies DWM rounded corners and the dark/light title tint to this window.</summary>
    private void ApplyBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = App.IsSystemLightTheme() ? 0 : 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    // ---- Detect when the user manually switches to a monitored VM ----

    private void SetupForegroundHook()
    {
        _winEventProc = OnForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        _foregroundHandle = hwnd;

        // The VM the user just left should drop out of "Viewing" immediately,
        // rather than lingering until the next poll. Mark it so the next scan
        // rebaselines it: any pixel changes while they looked were theirs, not the VM's.
        if (_viewingItem is not null && _viewingItem.Handle != hwnd)
        {
            if (_viewingItem.Status == "Viewing")
                _viewingItem.Status = "Idle";
            _viewedSinceScan.Add(_viewingItem.Handle);
            _viewingItem = null;
        }

        // If the user just brought a tracked VM to the front, clear its alert now
        // and float it to the top of the list as the most-recently-used.
        if (_byHandle.TryGetValue(hwnd, out var item))
        {
            item.Status = "Viewing";
            item.Order = ++_orderSeq;
            _viewingItem = item;
            _viewedSinceScan.Add(hwnd);
            MarkCurrent(item);
            ResortWindows();
        }

        // Focus changes can clear (or reveal) the last Active window, so refresh
        // the tray colour immediately instead of waiting for the next poll.
        RecomputeTrayIcon();
    }

    /// <summary>Marks <paramref name="item"/> as the most-recently-viewed window,
    /// clearing the highlight border from whichever card held it before.</summary>
    private void MarkCurrent(MonitoredWindow item)
    {
        if (ReferenceEquals(_currentItem, item))
            return;
        if (_currentItem is not null)
            _currentItem.IsCurrent = false;
        item.IsCurrent = true;
        _currentItem = item;
    }

    /// <summary>Reorders the list so pinned cards sit on top, idle cards sink to the
    /// bottom, and the rest are ordered by most recently opened/activated.</summary>
    private void ResortWindows()
    {
        var sorted = _windows
            .OrderByDescending(w => w.IsPinned)
            .ThenBy(w => w.IsIdle)
            .ThenByDescending(w => w.Order)
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = _windows.IndexOf(sorted[i]);
            if (current != i)
                _windows.Move(current, i);
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredWindow item)
        {
            item.IsPinned = !item.IsPinned;
            // Pinning and marking idle are opposites; turning one on clears the other.
            if (item.IsPinned)
                item.IsIdle = false;
            ResortWindows();
            WindowMemory.Remember(item);
        }
        e.Handled = true;
    }

    private void IdleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredWindow item)
        {
            item.IsIdle = !item.IsIdle;
            // Marking idle is the opposite of pinning; turning one on clears the other.
            if (item.IsIdle)
                item.IsPinned = false;
            ResortWindows();
            WindowMemory.Remember(item);
        }
        e.Handled = true;
    }

    // ---- Inline card rename ----

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredWindow item)
        {
            item.IsEditing = !item.IsEditing;
            // Closing the editor commits the name; persist it.
            if (!item.IsEditing)
                WindowMemory.Remember(item);
        }
        e.Handled = true;
    }

    private void NameEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.IsVisible)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void NameEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape
            && sender is FrameworkElement fe && fe.DataContext is MonitoredWindow item)
        {
            // Committing collapses the TextBox, which moves focus into the row and
            // would otherwise select it (and foreground the VM). Suppress that.
            _suppressNextSelection = true;
            item.IsEditing = false;
            WindowMemory.Remember(item);
            e.Handled = true;
        }
    }

    private void NameEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        // If focus is leaving because the user clicked this card's check or clear
        // button, let those handlers manage state (avoids prematurely closing).
        if (MouseOverEditButton()) return;
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredWindow item)
        {
            item.IsEditing = false;
            WindowMemory.Remember(item);
        }
    }

    private void ClearEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredWindow item)
        {
            item.CustomName = "";
            // Keep editing and return focus to the input for further typing.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = FindNameEditFor(item);
                tb?.Focus();
            }), DispatcherPriority.Input);
        }
        e.Handled = true;
    }

    /// <summary>Locates the visible rename TextBox bound to the given item.</summary>
    private System.Windows.Controls.TextBox? FindNameEditFor(MonitoredWindow item)
    {
        var container = WindowsList.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject;
        return container is null ? null : FindVisualChild<System.Windows.Controls.TextBox>(container, "NameEdit");
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name)
                return fe;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private bool MouseOverEditButton()
    {
        var editStyle = (Style)FindResource("EditButton");
        var clearStyle = (Style)FindResource("ClearButton");
        DependencyObject? d = Mouse.DirectlyOver as DependencyObject;
        while (d is not null)
        {
            if (d is System.Windows.Controls.Button b && (b.Style == editStyle || b.Style == clearStyle))
                return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ---- Tray flyout ----

    private void SetupTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFlyout());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIconImage = CreateTrayIcon(false);
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIconImage,
            Visible = true,
            Text = "Nimbus",
            ContextMenuStrip = menu
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ToggleFlyout();
        };
    }

    /// <summary>Swaps the tray icon to its alert (green) variant when any VM is Active.</summary>
    private void RecomputeTrayIcon() =>
        UpdateTrayIcon(_windows.Any(w => w.Status == "Active"));

    private void UpdateTrayIcon(bool active)
    {
        if (_trayIcon is null || active == _trayActive) return;
        _trayActive = active;
        var fresh = CreateTrayIcon(active);
        _trayIcon.Icon = fresh;
        _trayIconImage?.Dispose();
        _trayIconImage = fresh;
    }

    /// <summary>Draws a small monitor-glyph tray icon at runtime (no asset file needed).</summary>
    private static Drawing.Icon CreateTrayIcon(bool active)
    {
        const int size = 32;
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            // Background goes green when a tracked VM is Active, blue otherwise.
            string bgColor = active ? "#1E9E54" : "#2D6CDF";
            using (var bg = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(bgColor)))
            using (var bgPath = RoundedRect(new Drawing.Rectangle(0, 0, 32, 32), 4))
                g.FillPath(bg, bgPath);

            // Simple white cloud glyph: three puffs over a flat rounded base.
            using (var white = new Drawing.SolidBrush(Drawing.Color.White))
            {
                g.FillEllipse(white, 5, 13, 12, 12);     // left puff
                g.FillEllipse(white, 12, 8, 15, 15);     // top puff
                g.FillEllipse(white, 18, 14, 10, 10);    // right puff
                using (var baseRect = RoundedRect(new Drawing.Rectangle(6, 17, 20, 8), 4))
                    g.FillPath(white, baseRect);
            }
        }

        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var tmp = Drawing.Icon.FromHandle(hicon);
            return (Drawing.Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void ToggleFlyout()
    {
        if (IsVisible) HideFlyout();
        else ShowFlyout();
    }

    private void ShowFlyout()
    {
        Show();
        PositionToTray();
        Activate();
    }

    private void HideFlyout()
    {
        CancelAllEditing();
        AdvancedPanel.Visibility = Visibility.Collapsed;
        Hide();
        // Now that the UI is hidden, hand the bulk of our working set back to the
        // OS — a tray app shouldn't sit on ~100 MB of resident pages while idle.
        TrimWorkingSet();
    }

    /// <summary>Releases paged-out memory back to Windows to shrink the resident footprint.</summary>
    private static void TrimWorkingSet()
    {
        try
        {
            NativeMethods.SetProcessWorkingSetSize(
                NativeMethods.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { /* best-effort; ignore if the OS declines */ }
    }

    /// <summary>Drops any in-progress card renames so they don't persist when reopened.</summary>
    private void CancelAllEditing()
    {
        foreach (var w in _windows)
            w.IsEditing = false;
    }

    private void PositionToTray()
    {
        var wa = SystemParameters.WorkArea;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        Left = wa.Right - Width - 12;
        Top = wa.Bottom - h - 12;
    }

    private void ExitApp()
    {
        _exiting = true;
        _timer.Stop();
        if (_winEventHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_winEventHook);
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Behave like a tray flyout: tuck away when focus moves elsewhere.
        if (!_exiting && IsVisible)
            HideFlyout();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing (e.g. Alt+F4) hides to tray instead of exiting.
        if (!_exiting)
        {
            e.Cancel = true;
            HideFlyout();
        }
        base.OnClosing(e);
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* button not held */ }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideFlyout();

    private void AdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        AdvancedPanel.Visibility = AdvancedPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ---- Launch at Windows startup (per-user Run key) ----

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "Nimbus";

    private static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(StartupValueName) is string;
    }

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartupCheck.IsChecked == true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;
            if (enable)
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(StartupValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Re-sync the checkbox to the actual registry state if the write failed.
            StartupCheck.IsChecked = IsStartupEnabled();
        }
    }

    // Click a row to bring that VM window to the foreground.
    private void WindowsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // While any card is being renamed, a click on the list should only dismiss
        // the edit (commit) and never foreground a VM.
        if (_windows.Any(w => w.IsEditing))
            _suppressNextSelection = true;
    }

    private void WindowsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (WindowsList.SelectedItem is not MonitoredWindow item) return;
        // A rename just committed; ignore the focus-driven selection it caused.
        if (_suppressNextSelection)
        {
            _suppressNextSelection = false;
            WindowsList.SelectedItem = null;
            return;
        }
        // Clicking into the rename box selects the row; don't steal focus to the VM.
        if (item.IsEditing)
        {
            WindowsList.SelectedItem = null;
            return;
        }
        if (NativeMethods.IsIconic(item.Handle))
            NativeMethods.ShowWindow(item.Handle, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(item.Handle);
        WindowsList.SelectedItem = null;
    }

    private void ApplyInterval()
    {
        int seconds = 10;
        if (int.TryParse(IntervalBox.Text, out int parsed) && parsed >= 2)
            seconds = parsed;
        var mainInterval = TimeSpan.FromSeconds(seconds);
        // Reassigning a running DispatcherTimer's Interval restarts its countdown,
        // so only touch it when the value actually changed.
        if (_timer.Interval != mainInterval)
            _timer.Interval = mainInterval;

        int updateSeconds = 60;
        if (int.TryParse(UpdateIntervalBox.Text, out int parsedUpdate) && parsedUpdate >= 5)
            updateSeconds = parsedUpdate;
        var updateInterval = TimeSpan.FromSeconds(updateSeconds);
        if (_updateTimer.Interval != updateInterval)
            _updateTimer.Interval = updateInterval;

        // Keep the OCR title-ignore list in sync with the user's edits.
        ScreenText.SetIgnoredTitles(IgnoreBox.Text);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ClearMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        WindowMemory.Clear();

        // Reset the live list so the cleared state shows immediately: drop every
        // card and the cached signatures, then re-scan from scratch.
        _windows.Clear();
        _byHandle.Clear();
        _signatures.Clear();
        _titleSignatures.Clear();
        _viewedSinceScan.Clear();
        _viewingItem = null;
        _currentItem = null;

        StatusBar.Text = $"Memory cleared · {DateTime.Now:HH:mm:ss}";
        _ = RefreshAsync();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isPolling = !_isPolling;
        if (_isPolling)
        {
            ApplyInterval();
            _timer.Start();
            _updateTimer.Start();
            ToggleButton.Content = "Pause";
        }
        else
        {
            _timer.Stop();
            _updateTimer.Stop();
            ToggleButton.Content = "Resume";
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshInProgress) return;
        _refreshInProgress = true;
        ApplyInterval();

        try
        {
            string filter = FilterBox.Text;
            IntPtr self = new WindowInteropHelper(this).Handle;

            // Snapshot the last-known activity signature per window so the background
            // pass can decide whether OCR is even necessary.
            var prevSignatures = new Dictionary<IntPtr, byte[]>(_signatures);

            // Enumerate + capture off the UI thread. OCR runs ONLY when the body
            // region changed meaningfully since the last poll.
            var snapshot = await Task.Run(() =>
            {
                var infos = WindowCapture.Enumerate(filter, self);
                var captured = new List<(WindowInfo Info, bool HasFrame, string? Title, byte[] Sig, byte[] TitleSig, bool IsNew, bool Changed)>();
                foreach (var info in infos)
                {
                    var frame = WindowCapture.Capture(info.Handle);
                    if (frame is null)
                    {
                        captured.Add((info, false, null, Array.Empty<byte>(), Array.Empty<byte>(), false, false));
                        continue;
                    }

                    byte[] sig = ScreenText.ComputeActivitySignature(frame);
                    prevSignatures.TryGetValue(info.Handle, out byte[]? prev);
                    bool isNew = prev is null;
                    bool changed = ScreenText.SignificantlyDiffers(prev, sig);

                    // Only OCR on first sight; periodic title refreshes are handled
                    // separately by the slower "check for updates" pass. Seed the title
                    // signature so that pass can skip OCR until the header band changes.
                    string? title = null;
                    byte[] titleSig = Array.Empty<byte>();
                    if (isNew)
                    {
                        title = ScreenText.ReadTitleAsync(frame).GetAwaiter().GetResult();
                        titleSig = ScreenText.ComputeTitleSignature(frame);
                    }

                    captured.Add((info, true, title, sig, titleSig, isNew, changed));
                }
                return captured;
            });

            var seen = new HashSet<IntPtr>();
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            foreach (var (info, hasFrame, title, sig, titleSig, isNew, changed) in snapshot)
            {
                seen.Add(info.Handle);

                if (!_byHandle.TryGetValue(info.Handle, out var item))
                {
                    item = new MonitoredWindow(info.Handle, info.Title, info.ProcessName);
                    item.Order = ++_orderSeq;
                    // Restore what we last knew about this window: its name, pin/idle
                    // state, and the activity title — keyed by the unique window title
                    // so each VM is remembered separately.
                    var mem = WindowMemory.Get(info.Title);
                    if (mem is not null)
                    {
                        item.CustomName = mem.CustomName;
                        item.IsPinned = mem.IsPinned;
                        item.IsIdle = mem.IsIdle;
                        if (mem.History is { Count: > 0 })
                            item.SeedHistory(mem.History);
                        if (!string.IsNullOrWhiteSpace(mem.LastTitle))
                            item.Focus = mem.LastTitle;
                    }
                    _byHandle[info.Handle] = item;
                    _windows.Add(item);
                }

                item.Title = info.Title;
                item.ProcessName = info.ProcessName;

                if (!hasFrame)
                {
                    item.Status = "Minimized";
                }
                else if (info.Handle == foreground)
                {
                    // The user is actively viewing this VM: clear the alert and
                    // rebaseline so it only flags Active on changes after they leave.
                    item.Status = "Viewing";
                    _viewingItem = item;
                    _signatures[info.Handle] = sig;
                }
                else if (_viewedSinceScan.Contains(info.Handle))
                {
                    // The user manually opened/viewed this VM since the last scan, so
                    // the snapshot moved because of them. Rebaseline silently to the
                    // current frame instead of reporting Active.
                    item.Status = "Idle";
                    _signatures[info.Handle] = sig;
                }
                else
                {
                    item.Status = isNew ? "Captured" : changed ? "Active" : "Idle";
                    _signatures[info.Handle] = sig;
                }

                // Title is refreshed only when OCR ran; otherwise the prior one stands.
                if (hasFrame && !string.IsNullOrWhiteSpace(title))
                {
                    item.Focus = title;
                    item.FullText = title;
                    WindowMemory.Remember(item);
                }

                // Seed the header signature for freshly captured windows so the title
                // pass can skip OCR until the header actually changes.
                if (isNew && hasFrame)
                    _titleSignatures[info.Handle] = titleSig;

                item.LastUpdated = DateTime.Now;
            }

            // Manual-view rebaselines have been consumed for this scan.
            _viewedSinceScan.Clear();

            // Drop windows that have closed since the last poll.
            foreach (var handle in _byHandle.Keys.ToList())
            {
                if (!seen.Contains(handle))
                {
                    if (ReferenceEquals(_viewingItem, _byHandle[handle]))
                        _viewingItem = null;
                    _windows.Remove(_byHandle[handle]);
                    _byHandle.Remove(handle);
                    _signatures.Remove(handle);
                    _titleSignatures.Remove(handle);
                }
            }

            // Keep the most recently opened/activated VM at the top.
            ResortWindows();

            StatusBar.Text = $"Tracking {_windows.Count} window(s) · last scan {DateTime.Now:HH:mm:ss} · " +
                             $"refreshing every {_timer.Interval.TotalSeconds:0}s" +
                             (_isPolling ? "" : " · paused") +
                             (ScreenText.IsAvailable ? "" : " · OCR unavailable (install a language pack for chat titles)");

            UpdateTrayIcon(_windows.Any(w => w.Status == "Active"));
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    /// <summary>
    /// Slower pass that re-reads the chat title for every tracked window so the
    /// displayed titles stay current even when the body pixels barely moved.
    /// </summary>
    private async Task CheckUpdatesAsync()
    {
        if (_updateInProgress || _exiting) return;
        _updateInProgress = true;

        try
        {
            string filter = FilterBox.Text;
            IntPtr self = new WindowInteropHelper(this).Handle;

            // Snapshot the last-known header signatures so the background pass can
            // decide, per window, whether the title is worth re-OCRing at all.
            var prevTitleSig = new Dictionary<IntPtr, byte[]>(_titleSignatures);

            var titles = await Task.Run(() =>
            {
                var results = new List<(IntPtr Handle, string Title, byte[] Sig, bool Ocred)>();
                foreach (var info in WindowCapture.Enumerate(filter, self))
                {
                    var frame = WindowCapture.Capture(info.Handle);
                    if (frame is null) continue;

                    // OCR is the expensive part — only run it when the header band moved.
                    byte[] sig = ScreenText.ComputeTitleSignature(frame);
                    prevTitleSig.TryGetValue(info.Handle, out byte[]? prev);
                    bool ocr = prev is null || ScreenText.SignificantlyDiffers(prev, sig);
                    string title = ocr
                        ? ScreenText.ReadTitleAsync(frame).GetAwaiter().GetResult()
                        : string.Empty;
                    results.Add((info.Handle, title, sig, ocr));
                }
                return results;
            });

            foreach (var (handle, title, sig, ocred) in titles)
            {
                _titleSignatures[handle] = sig;
                if (ocred && !string.IsNullOrWhiteSpace(title) && _byHandle.TryGetValue(handle, out var item))
                {
                    item.Focus = title;
                    item.FullText = title;
                    WindowMemory.Remember(item);
                }
            }
        }
        catch
        {
            // Title refresh is best-effort; ignore transient capture/OCR failures.
        }
        finally
        {
            _updateInProgress = false;
        }
    }
}
