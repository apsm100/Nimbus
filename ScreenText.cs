using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace VmMonitor;

/// <summary>
/// Reads on-screen text from a captured frame using the on-device Windows OCR
/// engine. This is the only way to learn "what the VM is doing", because an
/// msrdc remote session exposes pixels only — never the inner app's UI tree.
/// </summary>
public static class ScreenText
{
    private static readonly OcrEngine? Engine = OcrEngine.TryCreateFromUserProfileLanguages();

    public static bool IsAvailable => Engine is not null;

    /// <summary>Title shown in the UI plus the chat body text used only for change detection.</summary>
    public readonly record struct ChatRead(string Title, string Body);

    /// <summary>
    /// Tokens that prove the captured region really is the VS Code chat panel. Unless
    /// one of these is recognised in the header, we assume the window is showing
    /// something else (a different app, or another VS Code view) and refuse to read a
    /// title — this stops titles from updating when we're not in the right window.
    /// </summary>
    private static readonly string[] SignatureTokens = { "CHAT" };

    /// <summary>
    /// Headings that belong to the chat UI chrome rather than an actual chat title.
    /// "Sessions" is the main listing header, so reading it as a title is meaningless.
    /// User-editable via <see cref="SetIgnoredTitles"/>; matched case-insensitively
    /// against the whole extracted title.
    /// </summary>
    private static string[] _ignoredTitles = { "sessions" };

    /// <summary>The current ignore list (read-only view).</summary>
    public static IReadOnlyList<string> IgnoredTitles => _ignoredTitles;

    /// <summary>Default ignore keywords, shown when the user hasn't customised the list.</summary>
    public const string DefaultIgnoredTitles = "sessions";

    /// <summary>
    /// Replaces the ignore list from a user-entered string. Entries may be separated
    /// by commas or new lines; blank entries are dropped.
    /// </summary>
    public static void SetIgnoredTitles(string? raw)
    {
        _ignoredTitles = (raw ?? string.Empty)
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    // Region of the captured window that holds the chat title (header band).
    private const double HeaderX = 0.58, HeaderY = 0.035, HeaderW = 0.42, HeaderH = 0.085;
    // Region used only to detect activity (the conversation body).
    private const double BodyX = 0.58, BodyY = 0.13, BodyW = 0.42, BodyH = 0.40;

    /// <summary>
    /// Cheap downscaled-grayscale signature of the chat body region. Comparing two
    /// signatures with <see cref="SignificantlyDiffers"/> tells us whether anything
    /// changed WITHOUT running OCR, while tolerating remote-desktop compression noise.
    /// </summary>
    public static byte[] ComputeActivitySignature(BitmapSource source) =>
        ComputeRegionSignature(source, BodyX, BodyY, BodyW, BodyH);

    /// <summary>
    /// Cheap signature of the header (title) band. Lets the periodic title pass skip
    /// the expensive OCR call whenever the title hasn't visibly changed.
    /// </summary>
    public static byte[] ComputeTitleSignature(BitmapSource source) =>
        ComputeRegionSignature(source, HeaderX, HeaderY, HeaderW, HeaderH);

    private static byte[] ComputeRegionSignature(BitmapSource source,
        double relX, double relY, double relW, double relH)
    {
        int fullW = source.PixelWidth;
        int fullH = source.PixelHeight;
        if (fullW < 50 || fullH < 50) return Array.Empty<byte>();

        int x = (int)(fullW * relX);
        int y = (int)(fullH * relY);
        int w = Math.Max(1, Math.Min(fullW - x, (int)(fullW * relW)));
        int h = Math.Max(1, Math.Min(fullH - y, (int)(fullH * relH)));

        BitmapSource region = new CroppedBitmap(source, new Int32Rect(x, y, w, h));

        const int gw = 96, gh = 72;
        var scaled = new TransformedBitmap(region,
            new ScaleTransform((double)gw / w, (double)gh / h));
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

        int stride = gw;
        var pixels = new byte[stride * gh];
        gray.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// True when two activity signatures differ enough to count as a real change.
    /// A per-pixel tolerance absorbs compression/anti-aliasing jitter, and a minimum
    /// fraction of changed pixels avoids reacting to a blinking cursor or a stray glyph.
    /// </summary>
    public static bool SignificantlyDiffers(byte[]? previous, byte[]? current)
    {
        if (previous is null || current is null) return false;
        if (previous.Length == 0 || current.Length != previous.Length) return true;

        const int pixelTolerance = 16;   // ignore brightness wobble up to this much
        int changed = 0;
        for (int i = 0; i < current.Length; i++)
        {
            if (Math.Abs(current[i] - previous[i]) > pixelTolerance)
                changed++;
        }

        // Require at least ~1.5% of the sampled pixels to move before flagging Active.
        return changed > current.Length * 0.015;
    }

    /// <summary>OCRs only the header band and returns the cleaned chat title.</summary>
    public static async Task<string> ReadTitleAsync(BitmapSource source)
    {
        if (Engine is null) return string.Empty;
        if (source.PixelWidth < 50 || source.PixelHeight < 50) return string.Empty;

        string header = await OcrRegionAsync(source, HeaderX, HeaderY, HeaderW, HeaderH);
        return ExtractTitle(header);
    }

    /// <summary>
    /// Reads the active chat title (the line next to the back arrow) and, separately,
    /// the chat conversation body. The body is never displayed — it is hashed to tell
    /// whether the session is doing something (Active) or static (Idle).
    /// </summary>
    public static async Task<ChatRead> ReadChatAsync(BitmapSource source)
    {
        if (Engine is null) return default;

        int w = source.PixelWidth;
        int h = source.PixelHeight;
        if (w < 50 || h < 50) return default;

        // Chat header band (the "CHAT" tab + title row); excludes the body below.
        string header = await OcrRegionAsync(source, HeaderX, HeaderY, HeaderW, HeaderH);
        string title = ExtractTitle(header);

        // Conversation area below the header — used only for activity detection.
        string body = await OcrRegionAsync(source, BodyX, BodyY, BodyW, BodyH);

        return new ChatRead(title, body);
    }

    private static async Task<string> OcrRegionAsync(BitmapSource source,
        double relX, double relY, double relW, double relH)
    {
        int fullW = source.PixelWidth;
        int fullH = source.PixelHeight;

        int x = (int)(fullW * relX);
        int y = (int)(fullH * relY);
        int w = Math.Max(1, Math.Min(fullW - x, (int)(fullW * relW)));
        int h = Math.Max(1, Math.Min(fullH - y, (int)(fullH * relH)));

        BitmapSource region = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        // Upscale small text 2x for more reliable recognition.
        region = new TransformedBitmap(region, new ScaleTransform(2.0, 2.0));
        return await RunOcrAsync(region);
    }

    /// <summary>Order-independent hash of the letters/digits in text, for change detection.</summary>
    public static long HashText(string text)
    {
        long hash = unchecked((long)1469598103934665603UL);
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            hash ^= char.ToLowerInvariant(c);
            hash *= 1099511628211L;
        }
        return hash;
    }

    private static async Task<string> RunOcrAsync(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        IBuffer buffer = CryptographicBuffer.CreateFromByteArray(pixels);
        using SoftwareBitmap bitmap =
            SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height);

        OcrResult result = await Engine!.RecognizeAsync(bitmap);
        return result.Text ?? string.Empty;
    }

    /// <summary>
    /// Extracts the chat title from the OCR'd header. The chat panel renders the
    /// "CHAT" tab label immediately before the title, so we take the text after the
    /// last "CHAT" token (dropping any editor-toolbar noise that precedes it) and
    /// strip the back arrow / icon glyphs. When no signature token is present we
    /// return empty, signalling "not the chat window — don't touch the title".
    /// </summary>
    private static string ExtractTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string flat = Regex.Replace(text.Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();
        if (flat.Length == 0) return string.Empty;

        // Signature gate: only trust this as a chat title when the panel's tab label
        // was recognised. Without it we're almost certainly looking at another window.
        var chatMatches = FindSignature(flat);
        if (chatMatches is null)
            return string.Empty;

        string candidate = CleanEnds(flat[(chatMatches.Index + chatMatches.Length)..]);

        // If nothing meaningful followed the tab label, fall back to the header minus it.
        if (candidate.Count(char.IsLetter) < 3)
            candidate = CleanEnds(StripSignature(flat));

        // Drop UI chrome headings (e.g. the "Sessions" listing header) that aren't titles.
        if (IsIgnoredTitle(candidate))
            return string.Empty;

        return candidate.Length > 140 ? candidate[..140] + "…" : candidate;
    }

    /// <summary>Returns the last signature-token match in the header, or null if none appear.</summary>
    private static Match? FindSignature(string flat)
    {
        Match? last = null;
        foreach (string token in SignatureTokens)
        {
            var matches = Regex.Matches(flat, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase);
            if (matches.Count > 0 && (last is null || matches[^1].Index > last.Index))
                last = matches[^1];
        }
        return last;
    }

    /// <summary>Removes every signature token from the header text.</summary>
    private static string StripSignature(string flat)
    {
        foreach (string token in SignatureTokens)
            flat = Regex.Replace(flat, $@"\b{Regex.Escape(token)}\b", "", RegexOptions.IgnoreCase);
        return flat;
    }

    /// <summary>True when the extracted title is just a UI heading we want to ignore.</summary>
    private static bool IsIgnoredTitle(string candidate)
    {
        string trimmed = candidate.Trim();
        foreach (string ignored in IgnoredTitles)
            if (string.Equals(trimmed, ignored, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string CleanEnds(string s)
    {
        s = Regex.Replace(s, @"^[^\p{L}\p{N}]+", "");        // leading arrow / icons
        s = Regex.Replace(s, @"[^\p{L}\p{N})\]]+$", "");      // trailing icons
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }
}
