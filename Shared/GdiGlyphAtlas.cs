using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MatrixDesktop.Shared;

internal static class GdiGlyphAtlas
{
    internal const long MaximumRasterWorkPixels = 67_108_864;
    internal sealed record Atlas(object Metadata, Dictionary<string, byte[]> Resources);
    private sealed record Slice(string Text, byte[] Pixels, int Width, int Height, double OffsetX, double OffsetY, double AdvanceX, double AdvanceY);

    internal static void ValidateRasterWork(int side, int glyphCount)
    {
        if ((long)side * side * glyphCount > MaximumRasterWorkPixels)
            throw new InvalidOperationException("The native font raster work exceeds its 64-million-pixel budget; use a smaller font or repertoire.");
    }

    internal static Atlas Create(string? fontJson, IEnumerable<string> repertoire, bool special, int dpi,
        string? privateFontPath, string idPrefix, Func<string, string> resourceUrl)
    {
        dpi = Math.Clamp(dpi, 96, 384);
        var scale = dpi / 96.0;
        var warnings = new List<string>();
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(fontJson) ? "{}" : fontJson);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("A native font descriptor must be an object.");
        int Number(string key, int fallback, int minimum, int maximum)
        {
            if (!root.TryGetProperty(key, out var value)) return fallback;
            if (!value.TryGetInt32(out var result) || result < minimum || result > maximum)
                throw new InvalidOperationException($"Native font {key} is outside {minimum}..{maximum}.");
            return result;
        }
        byte Boolean(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True ? (byte)1 : (byte)0;
        var name = root.TryGetProperty("name", out var face) && face.ValueKind == JsonValueKind.String ? face.GetString()! : special ? "Terminal" : "Matrix Code Font";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 31) throw new InvalidOperationException("A native font name must contain 1..31 characters.");
        var height = Number("height", special ? -8 : -7, -256, 256);
        var width = Number("width", 0, 0, 256);
        var escapement = Number("escapement", 0, -3600, 3600);
        var orientation = Number("orientation", 0, -3600, 3600);
        var font = new LogFont
        {
            Height = (int)Math.Round(height * scale), Width = (int)Math.Round(width * scale),
            Escapement = escapement, Orientation = orientation, Weight = Number("weight", 400, 0, 1000),
            Italic = Boolean("italic"), Underline = Boolean("underline"), StrikeOut = Boolean("strikeOut"),
            CharSet = (byte)Number("charSet", 1, 0, 255), OutPrecision = (byte)Number("outPrecision", 0, 0, 255),
            ClipPrecision = (byte)Number("clipPrecision", 0, 0, 255), Quality = (byte)Number("quality", 0, 0, 255),
            PitchAndFamily = (byte)(Number("pitch", 0, 0, 3) | Number("family", 0, 0, 240)), FaceName = name,
        };
        var characters = repertoire.Distinct(StringComparer.Ordinal).ToArray();
        var limit = special ? 1024 : 512;
        if (characters.Length > limit) throw new InvalidOperationException($"The native font repertoire exceeds {limit} characters.");
        if (characters.Any(text => text.Length != 1 || char.IsSurrogate(text[0])))
            throw new InvalidOperationException("Native ZMatrix fonts require individual BMP characters.");
        var registered = false;
        if (!string.IsNullOrEmpty(privateFontPath) && File.Exists(privateFontPath))
        {
            registered = AddFontResourceEx(privateFontPath, 0x10, 0) > 0;
            if (!registered) warnings.Add("The bundled Matrix font could not be registered privately.");
        }
        var dc = CreateCompatibleDC(0);
        if (dc == 0)
        {
            if (registered) RemoveFontResourceEx(privateFontPath!, 0x10, 0);
            throw new InvalidOperationException("A native font device context could not be created.");
        }
        nint nativeFont = 0, previousFont = 0;
        var pages = new List<Bitmap>();
        try
        {
            nativeFont = CreateFontIndirect(ref font);
            if (nativeFont == 0) throw new InvalidOperationException("Windows could not create the requested native font.");
            previousFont = SelectObject(dc, nativeFont);
            if (!GetTextMetrics(dc, out var metrics)) throw new InvalidOperationException("Windows could not measure the requested native font.");
            var resolved = new StringBuilder(256);
            GetTextFace(dc, resolved.Capacity, resolved);
            if (!name.Equals(resolved.ToString(), StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Font '{name}' was substituted with '{resolved}'.");
            _ = SetGraphicsMode(dc, 2);
            _ = SetBkMode(dc, 1);
            _ = SetTextColor(dc, 0x00ffffff);
            _ = SetTextAlign(dc, 6); // TA_TOP | TA_CENTER, matching the source renderer.
            var side = Math.Clamp(4 * (metrics.Height + metrics.MaxCharWidth + Math.Abs(metrics.Overhang) + 8), 64, 4096);
            ValidateRasterWork(side, characters.Length);
            var slices = new List<Slice>();
            long pixels = 0;
            foreach (var text in characters)
            {
                var slice = Rasterize(dc, text, side, scale, escapement);
                pixels += (long)slice.Width * slice.Height;
                if (pixels > 16_777_216) throw new InvalidOperationException("The native font atlas exceeds its 16-million-pixel budget.");
                slices.Add(slice);
            }
            var glyphs = new List<object>();
            var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var pageMetadata = new List<object>();
            var usedSizes = new List<Size>();
            var pageIndex = 0; var x = 1; var y = 1; var rowHeight = 0;
            Bitmap NewPage()
            {
                if (pages.Count >= 4) throw new InvalidOperationException("The native font atlas requires more than four pages.");
                var page = new Bitmap(2048, 2048, PixelFormat.Format32bppArgb);
                pages.Add(page); usedSizes.Add(new Size(1, 1)); return page;
            }
            var current = NewPage();
            foreach (var slice in slices)
            {
                var gx = 0; var gy = 0;
                if (slice.Width > 0 && slice.Height > 0)
                {
                    if (slice.Width + 2 > 2048 || slice.Height + 2 > 2048) throw new InvalidOperationException("A native glyph exceeds the atlas page size.");
                    if (x + slice.Width + 1 > 2048) { x = 1; y += rowHeight + 1; rowHeight = 0; }
                    if (y + slice.Height + 1 > 2048) { current = NewPage(); pageIndex++; x = y = 1; rowHeight = 0; }
                    gx = x; gy = y;
                    var rect = new Rectangle(x, y, slice.Width, slice.Height);
                    var data = current.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (var row = 0; row < slice.Height; row++)
                            Marshal.Copy(slice.Pixels, row * slice.Width * 4, data.Scan0 + row * data.Stride, slice.Width * 4);
                    }
                    finally { current.UnlockBits(data); }
                    x += slice.Width + 1; rowHeight = Math.Max(rowHeight, slice.Height);
                    usedSizes[pageIndex] = new Size(Math.Max(usedSizes[pageIndex].Width, x), Math.Max(usedSizes[pageIndex].Height, y + rowHeight + 1));
                }
                glyphs.Add(new { text = slice.Text, page = slice.Width == 0 ? 0 : pageIndex, x = gx, y = gy,
                    width = slice.Width, height = slice.Height, offsetX = slice.OffsetX, offsetY = slice.OffsetY,
                    advanceX = slice.AdvanceX, advanceY = slice.AdvanceY });
            }
            for (var i = 0; i < pages.Count; i++)
            {
                var size = usedSizes[i];
                using var cropped = pages[i].Clone(new Rectangle(Point.Empty, size), PixelFormat.Format32bppArgb);
                using var stream = new MemoryStream(); cropped.Save(stream, ImageFormat.Png);
                var id = $"{idPrefix}-{i}"; resources.Add(id, stream.ToArray());
                pageMetadata.Add(new { url = resourceUrl(id), width = size.Width, height = size.Height });
            }
            return new Atlas(new { atlasDpi = dpi, coverageMode = "rgb", pages = pageMetadata,
                metrics = new { height = metrics.Height / scale, maxCharWidth = metrics.MaxCharWidth / scale,
                    ascent = metrics.Ascent / scale, descent = metrics.Descent / scale,
                    internalLeading = metrics.InternalLeading / scale, externalLeading = metrics.ExternalLeading / scale },
                glyphs, requestedFont = root.Clone(), resolvedFace = resolved.ToString(), warnings }, resources);
        }
        finally
        {
            foreach (var page in pages) page.Dispose();
            if (previousFont != 0) SelectObject(dc, previousFont);
            if (nativeFont != 0) DeleteObject(nativeFont);
            DeleteDC(dc);
            if (registered) RemoveFontResourceEx(privateFontPath!, 0x10, 0);
        }
    }

    private static Slice Rasterize(nint dc, string text, int side, double scale, int escapement)
    {
        var info = new BitmapInfo { Size = 40, Width = side, Height = -side, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0) throw new InvalidOperationException("Windows could not allocate native glyph pixels.");
        var previous = SelectObject(dc, bitmap);
        try
        {
            var buffer = new byte[checked(side * side * 4)];
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            var anchor = side / 2;
            if (!TextOut(dc, anchor, anchor, text, text.Length)) throw new InvalidOperationException("Windows could not render a native glyph.");
            GdiFlush(); Marshal.Copy(bits, buffer, 0, buffer.Length);
            var left = side; var top = side; var right = -1; var bottom = -1;
            for (var y = 0; y < side; y++) for (var x = 0; x < side; x++)
            {
                var offset = (y * side + x) * 4;
                if ((buffer[offset] | buffer[offset + 1] | buffer[offset + 2]) == 0) continue;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            GetTextExtentPoint32(dc, text, text.Length, out var extent);
            var angle = escapement * Math.PI / 1800;
            var advanceX = Math.Cos(angle) * extent.Width / scale;
            var advanceY = -Math.Sin(angle) * extent.Width / scale;
            if (right < left) return new Slice(text, [], 0, 0, 0, 0, advanceX, advanceY);
            if (left == 0 || top == 0 || right == side - 1 || bottom == side - 1) throw new InvalidOperationException("The native glyph exceeded its measured raster bounds.");
            var width = right - left + 1; var height = bottom - top + 1;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var source = ((top + y) * side + left + x) * 4; var target = (y * width + x) * 4;
                pixels[target] = buffer[source]; pixels[target + 1] = buffer[source + 1]; pixels[target + 2] = buffer[source + 2];
                pixels[target + 3] = Math.Max(buffer[source], Math.Max(buffer[source + 1], buffer[source + 2]));
            }
            return new Slice(text, pixels, width, height, (left - anchor) / scale, (top - anchor) / scale, advanceX, advanceY);
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextMetric
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth, Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
        public ushort FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, CharSet;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontIndirectW")] private static extern nint CreateFontIndirect(ref LogFont font);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW")] private static extern bool GetTextMetrics(nint dc, out TextMetric metrics);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextFaceW")] private static extern int GetTextFace(nint dc, int count, StringBuilder face);
    [DllImport("gdi32.dll")] private static extern int SetGraphicsMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextAlign(nint dc, uint alignment);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")] private static extern bool TextOut(nint dc, int x, int y, string text, int count);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")] private static extern bool GetTextExtentPoint32(nint dc, string text, int count, out Size size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "AddFontResourceExW")] private static extern int AddFontResourceEx(string path, uint flags, nint reserved);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RemoveFontResourceExW")] private static extern bool RemoveFontResourceEx(string path, uint flags, nint reserved);
}
