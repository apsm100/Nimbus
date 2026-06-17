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

    // Region of the captured window that holds the chat title (header band).
    private const double HeaderX = 0.58, HeaderY = 0.035, HeaderW = 0.42, HeaderH = 0.085;
    // Region used only to detect activity (the conversation body).
    private const double BodyX = 0.58, BodyY = 0.13, BodyW = 0.42, BodyH = 0.40;

    /// <summary>
    /// Cheap downscaled-grayscale signature of the chat body region. Comparing two
    /// signatures with <see cref="SignificantlyDiffers"/> tells us whether anything
    /// changed WITHOUT running OCR, while tolerating remote-desktop compression noise.
    /// </summary>
    public static byte[] ComputeActivitySignature(BitmapSource source)
    {
        int fullW = source.PixelWidth;
        int fullH = source.PixelHeight;
        if (fullW < 50 || fullH < 50) return Array.Empty<byte>();

        int x = (int)(fullW * BodyX);
        int y = (int)(fullH * BodyY);
        int w = Math.Max(1, Math.Min(fullW - x, (int)(fullW * BodyW)));
        int h = Math.Max(1, Math.Min(fullH - y, (int)(fullH * BodyH)));

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
    /// strip the back arrow / icon glyphs.
    /// </summary>
    private static string ExtractTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string flat = Regex.Replace(text.Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();
        if (flat.Length == 0) return string.Empty;

        var chatMatches = Regex.Matches(flat, @"\bCHAT\b", RegexOptions.IgnoreCase);
        string candidate = chatMatches.Count > 0
            ? flat[(chatMatches[^1].Index + chatMatches[^1].Length)..]
            : flat;

        candidate = CleanEnds(candidate);

        // If nothing meaningful followed "CHAT", fall back to the header minus the tab label.
        if (candidate.Count(char.IsLetter) < 3 && chatMatches.Count > 0)
            candidate = CleanEnds(Regex.Replace(flat, @"\bCHAT\b", "", RegexOptions.IgnoreCase));

        return candidate.Length > 140 ? candidate[..140] + "…" : candidate;
    }

    private static string CleanEnds(string s)
    {
        s = Regex.Replace(s, @"^[^\p{L}\p{N}]+", "");        // leading arrow / icons
        s = Regex.Replace(s, @"[^\p{L}\p{N})\]]+$", "");      // trailing icons
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }
}
