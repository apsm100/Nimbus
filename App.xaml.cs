using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VmMonitor;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyTheme(IsSystemLightTheme());
        base.OnStartup(e);
        // Re-theme live when the user flips Windows between light and dark.
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General)
                Dispatcher.Invoke(() => ApplyTheme(IsSystemLightTheme()));
        };
    }

    /// <summary>Reads the current Windows taskbar/system theme (true = light, false = dark).</summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false; // default to dark
        }
    }

    /// <summary>Pushes the active palette into application resources used via DynamicResource.</summary>
    public static void ApplyTheme(bool light)
    {
        var p = light ? LightPalette : DarkPalette;
        var res = Current.Resources;
        foreach (var (key, hex) in p)
            res[key] = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    private static readonly (string Key, string Hex)[] DarkPalette =
    {
        ("WindowBg",            "#2C2C2E"),
        ("WindowBorder",        "#3C3C40"),
        ("CardBg",              "#353539"),
        ("CardHoverBg",         "#3F3F44"),
        ("CardSelectedBg",      "#38465C"),
        ("InputBg",             "#232326"),
        ("InputBorder",         "#46464C"),
        ("ToolBtnBg",           "#3A3A40"),
        ("ToolBtnHover",        "#46464C"),
        ("ToolBtnPressed",      "#313135"),
        ("DividerBrush",        "#3C3C42"),
        ("TextPrimary",         "#ECECEF"),
        ("TextSecondary",       "#9BA0A8"),
        ("TextSecondaryHover",  "#B0B4BC"),
        ("TextMuted",           "#A6A6AE"),
        ("AccentBrush",         "#2D6CDF"),
        ("AccentHoverBrush",    "#3B7BF0"),
        ("AccentPressedBrush",  "#255FC6"),
    };

    private static readonly (string Key, string Hex)[] LightPalette =
    {
        ("WindowBg",            "#F5F5F7"),
        ("WindowBorder",        "#D7D7DD"),
        ("CardBg",              "#FFFFFF"),
        ("CardHoverBg",         "#EDEDF2"),
        ("CardSelectedBg",      "#DCE7FB"),
        ("InputBg",             "#FFFFFF"),
        ("InputBorder",         "#C9C9D1"),
        ("ToolBtnBg",           "#E7E7EC"),
        ("ToolBtnHover",        "#DADAE1"),
        ("ToolBtnPressed",      "#CECED6"),
        ("DividerBrush",        "#E2E2E8"),
        ("TextPrimary",         "#1B1B22"),
        ("TextSecondary",       "#5A6470"),
        ("TextSecondaryHover",  "#3C434D"),
        ("TextMuted",           "#6A6A74"),
        ("AccentBrush",         "#2D6CDF"),
        ("AccentHoverBrush",    "#3B7BF0"),
        ("AccentPressedBrush",  "#255FC6"),
    };
}

