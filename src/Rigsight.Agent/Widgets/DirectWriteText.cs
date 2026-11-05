using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rigsight.Core;
#if RIGSIGHT_APP
// Linked into the app for the Taskbar page's preview, in a namespace of its own there (the tests see both assemblies).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>
/// The taskbar's words and numbers drawn by DirectWrite, Windows' own text engine (the taskbar's clock is drawn by it),
/// in greyscale with its natural, symmetric rendering: strokes snapped up and down, letters spaced as designed. GDI+
/// drew them softer than the clock beside them (the sharpness audit, 1.3: on the same background DirectWrite's text came
/// out about a third more solid). Called straight, through DirectWrite's own bitmap renderer and a font's glyphs (no WPF
/// in the agent: memory). The text is drawn white on black, its brightness taken as how much of each pixel it covers,
/// and put on the drawing in its colour. Anything it can't draw (no DirectWrite, a letter its font hasn't got) returns
/// false and the caller draws it as before.
/// </summary>
internal static class DirectWriteText
{
    [ThreadStatic] private static Engine? _engine;
    private static bool _failed;
    private static bool _logged;

    /// <summary>A line's width as DirectWrite lays it (null: it can't, measure as before).</summary>
    public static float? Width(string text, Font font)
    {
        if (text.Length == 0) return 0;
        var e = Get();
        if (e is null) return null;
        try { return e.Width(text, font); }
        catch (Exception ex) { Fail(ex); return null; }
    }

    /// <summary>Draws a line with its start at <paramref name="x"/> and its baseline at <paramref name="baseline"/>; false when it can't.</summary>
    /// <summary>
    /// Where a line's ink starts and ends from its pen's start (pixels): its width less the empty room the first letter
    /// keeps on its left and the last on its right ("°" keeps a lot), to centre what's seen (null: it can't).
    /// </summary>
    public static (float Left, float Right)? Ink(string text, Font font)
    {
        if (text.Length == 0) return null;
        var e = Get();
        if (e is null) return null;
        try { return e.Ink(text, font); }
        catch (Exception ex) { Fail(ex); return null; }
    }

    /// <summary>How many whole pixels across a line's solid ink takes as drawn (null: it can't), to size a tile it sits centred in.</summary>
    public static int? InkPixels(string text, Font font)
    {
        if (text.Length == 0) return 0;
        var e = Get();
        if (e is null) return null;
        try { return e.InkPixels(text, font); }
        catch (Exception ex) { Fail(ex); return null; }
    }

    /// <remarks><paramref name="halo"/>: an outline a pixel round the letters in that colour (words over a graph).</remarks>
    /// <remarks><paramref name="centre"/>: a span across (start, width) to put the ink in the middle of, by the pixels it really
    /// inked, moved by whole pixels only (a big number on its tile).</remarks>
    public static bool Draw(Graphics dest, string text, Font font, Color color, float x, int baseline, Color? halo = null, (float Left, float Width)? centre = null)
    {
        if (text.Length == 0 || color.A == 0) return true;
        var e = Get();
        if (e is null) return false;
        try { return e.Draw(dest, text, font, color, x, baseline, halo, centre); }
        catch (Exception ex) { Fail(ex); return false; }
    }

    private static Engine? Get()
    {
        if (_failed) return null;
        try { return _engine ??= new Engine(); }
        catch (Exception ex) { Fail(ex); return null; }
    }

    private static void Fail(Exception ex)
    {
        _failed = true;
        if (_logged) return;
        _logged = true;
        Log.Write("strip", $"DirectWrite text couldn't be drawn, so the taskbar draws it as before: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>One thread's DirectWrite: its factory, a bitmap to draw in, greyscale rendering, the faces of the fonts used.</summary>
    private sealed class Engine
    {
        private readonly IDWriteFactory _factory;
        private readonly IDWriteBitmapRenderTarget _target;
        private readonly IDWriteRenderingParams _params;
        private readonly IDWriteFontCollection _fonts;
        private readonly Dictionary<(string, float, FontStyle), Face?> _faces = [];
        private readonly Dictionary<(string, float, FontStyle, string), float> _widths = [];
        private int _w = 256, _h = 64;

        private sealed record Face(IDWriteFontFace FontFace, IntPtr Pointer, float Em, float UnitsPerEm);

        public Engine()
        {
            var iid = typeof(IDWriteFactory).GUID;
            Marshal.ThrowExceptionForHR(DWriteCreateFactory(0 /* shared */, ref iid, out var factory));
            _factory = (IDWriteFactory)factory;
            _factory.GetSystemFontCollection(out _fonts, false);
            _factory.GetGdiInterop(out var interop);
            interop.CreateBitmapRenderTarget(IntPtr.Zero, (uint)_w, (uint)_h, out _target);
            _target.SetPixelsPerDip(1);
            // Greyscale (no ClearType colour edges: the strip is a see-through layer), natural symmetric as XAML's text.
            _factory.CreateCustomRenderingParams(1.8f, 0.5f, 0f, 0 /* flat */, 5 /* natural symmetric */, out _params);
        }

        /// <summary>A font's DirectWrite face: its family as DirectWrite names it ("Segoe UI" at 600 for GDI's "Segoe UI Semibold").</summary>
        private Face? FaceOf(Font font)
        {
            var key = (font.FontFamily.Name, font.Size, font.Style);
            if (_faces.TryGetValue(key, out var made)) return made;
            string name = font.FontFamily.Name;
            int weight = font.Bold ? 700 : 400;
            foreach (var (suffix, w) in new[] { (" Semibold", 600), (" Semilight", 350), (" Light", 300), (" Black", 900) })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; weight = font.Bold ? Math.Max(w, 700) : w; }
            Face? face = null;
            _fonts.FindFamilyName(name, out uint index, out bool exists);
            if (!exists) _fonts.FindFamilyName(font.FontFamily.Name, out index, out exists);
            if (exists)
            {
                _fonts.GetFontFamily(index, out var family);
                family.GetFirstMatchingFont(weight, 5 /* normal stretch */, font.Italic ? 2 : 0, out var match);
                match.CreateFontFace(out var fontFace);
                fontFace.GetMetrics(out var metrics);
                face = new Face(fontFace, Marshal.GetComInterfaceForObject(fontFace, typeof(IDWriteFontFace)), font.Size, metrics.designUnitsPerEm);
            }
            return _faces[key] = face;
        }

        private readonly Dictionary<(string, float, FontStyle, string), int> _inkPixels = [];

        public int? InkPixels(string text, Font font)
        {
            var key = (font.FontFamily.Name, font.Size, font.Style, text);
            if (_inkPixels.TryGetValue(key, out int n)) return n;
            using var probe = new Bitmap(4, 4, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(probe);
            int? found = null;
            _lastInk = null;
            if (!Draw(g, text, font, Color.White, 0, 0, null, null)) return null;
            found = _lastInk;
            if (found is null) return null;
            if (_inkPixels.Count > 500) _inkPixels.Clear();
            return _inkPixels[key] = found.Value;
        }

        /// <summary>The solid ink's width of the last line drawn (columns over half covered).</summary>
        private int? _lastInk;

        public (float Left, float Right)? Ink(string text, Font font)
        {
            if (FaceOf(font) is not { } face || Glyphs(face, text) is not { } run) return null;
            float scale = face.Em / face.UnitsPerEm;
            return (run.Bearings.First * scale, run.Advances.Sum() - run.Bearings.Last * scale);
        }

        /// <summary>The text's glyphs and their advances (pixels), and its first letter's left and last letter's right empty room (design units); null when a letter has no glyph in the font.</summary>
        private (ushort[] Glyphs, float[] Advances, (int First, int Last) Bearings)? Glyphs(Face face, string text)
        {
            var points = new List<uint>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { points.Add((uint)char.ConvertToUtf32(text[i], text[i + 1])); i++; }
                else points.Add(text[i]);
            }
            var cps = points.ToArray();
            var glyphs = new ushort[cps.Length];
            var metrics = new DWRITE_GLYPH_METRICS[cps.Length];
            unsafe
            {
                fixed (uint* c = cps)
                fixed (ushort* g = glyphs)
                fixed (DWRITE_GLYPH_METRICS* m = metrics)
                {
                    face.FontFace.GetGlyphIndices(c, (uint)cps.Length, g);
                    for (int i = 0; i < glyphs.Length; i++) if (glyphs[i] == 0 && cps[i] != ' ') return null;
                    face.FontFace.GetDesignGlyphMetrics(g, (uint)glyphs.Length, m, false);
                }
            }
            var advances = metrics.Select(m => m.advanceWidth * face.Em / face.UnitsPerEm).ToArray();
            return (glyphs, advances, (metrics[0].leftSideBearing, metrics[^1].rightSideBearing));
        }

        public float? Width(string text, Font font)
        {
            var key = (font.FontFamily.Name, font.Size, font.Style, text);
            if (_widths.TryGetValue(key, out float w)) return w;
            if (FaceOf(font) is not { } face || Glyphs(face, text) is not { } run) return null;
            w = run.Advances.Sum();
            if (_widths.Count > 2000) _widths.Clear();
            return _widths[key] = w;
        }

        public bool Draw(Graphics dest, string text, Font font, Color color, float x, int baseline, Color? halo, (float Left, float Width)? centre)
        {
            if (FaceOf(font) is not { } face || Glyphs(face, text) is not { } run) return false;
            const int pad = 3;
            int ascent = (int)Math.Ceiling(face.Em * 1.1f), descent = (int)Math.Ceiling(face.Em * 0.4f);
            int w = (int)Math.Ceiling(run.Advances.Sum()) + 2 * pad + 2, h = ascent + descent + 2 * pad;
            if (w > _w || h > _h)
            {
                _w = Math.Max(_w, w);
                _h = Math.Max(_h, h);
                _target.Resize((uint)_w, (uint)_h);
            }
            IntPtr dc = _target.GetMemoryDC();
            PatBlt(dc, 0, 0, w, h, Blackness);
            // The start's fraction kept: natural rendering places letters between pixels across, never up and down.
            float fx = x - MathF.Floor(x);
            RECT inked;
            unsafe
            {
                fixed (ushort* g = run.Glyphs)
                fixed (float* a = run.Advances)
                {
                    var glyphRun = new DWRITE_GLYPH_RUN
                    {
                        fontFace = face.Pointer, fontEmSize = face.Em, glyphCount = (uint)run.Glyphs.Length,
                        glyphIndices = (IntPtr)g, glyphAdvances = (IntPtr)a, glyphOffsets = IntPtr.Zero, isSideways = 0, bidiLevel = 0,
                    };
                    _target.DrawGlyphRun(pad + fx, pad + ascent, 0 /* natural */, ref glyphRun, _params, 0x00FFFFFF, out inked);
                }
            }
            // What it drew, white on black: its brightness is how much each pixel is covered.
            // Centred by what it really inked: the whole pixels between the ink's middle and the span's.
            int shift = 0;
            if (centre is { } span && inked.R > inked.L)
            {
                float inkMiddle = MathF.Floor(x) - pad + (inked.L + inked.R) / 2f;
                shift = (int)MathF.Round(span.Left + span.Width / 2 - inkMiddle);
            }
            IntPtr bitmap = GetCurrentObject(dc, 7 /* OBJ_BITMAP */);
            if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero) return false;
            int stride = dib.dsBm.bmWidthBytes;
            var cover = new byte[w * h];
            {
                var line = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(dib.dsBm.bmBits + y * stride, line, 0, w * 4);
                    for (int i = 0; i < w; i++) cover[y * w + i] = Math.Max(line[i * 4], Math.Max(line[i * 4 + 1], line[i * 4 + 2]));
                }
            }
            {
                int first = -1, last = -1;
                for (int i = 0; i < w; i++)
                    for (int y = 0; y < h; y++)
                        if (cover[y * w + i] > 128) { if (first < 0) first = i; last = i; break; }
                _lastInk = first < 0 ? 0 : last - first + 1;
            }
            if (halo is { } around)
            {
                // The outline: each pixel as covered as the most covered next to it, in the outline's colour, under the letters.
                using var ring = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                var rd = ring.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row2 = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        for (int i = 0; i < w; i++)
                        {
                            int m = 0;
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int yy = y + dy, xx = i + dx;
                                    if (yy >= 0 && yy < h && xx >= 0 && xx < w) m = Math.Max(m, cover[yy * w + xx]);
                                }
                            int alpha = Math.Min(255, m * 2) * around.A / 255;
                            row2[i * 4] = (byte)(around.B * alpha / 255);
                            row2[i * 4 + 1] = (byte)(around.G * alpha / 255);
                            row2[i * 4 + 2] = (byte)(around.R * alpha / 255);
                            row2[i * 4 + 3] = (byte)alpha;
                        }
                        Marshal.Copy(row2, 0, rd.Scan0 + y * rd.Stride, w * 4);
                    }
                }
                finally { ring.UnlockBits(rd); }
                dest.DrawImage(ring, new Rectangle((int)MathF.Floor(x) - pad + shift, baseline - ascent - pad, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
            }
            using var ink = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = ink.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        int c = cover[y * w + i];
                        int alpha = c * color.A / 255;
                        row[i * 4] = (byte)(color.B * alpha / 255);
                        row[i * 4 + 1] = (byte)(color.G * alpha / 255);
                        row[i * 4 + 2] = (byte)(color.R * alpha / 255);
                        row[i * 4 + 3] = (byte)alpha;
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w * 4);
                }
            }
            finally { ink.UnlockBits(data); }
            dest.DrawImage(ink, new Rectangle((int)MathF.Floor(x) - pad + shift, baseline - ascent - pad, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
            return true;
        }
    }

    // ── DirectWrite, as dwrite.h lays it out (only what's used is typed; the rest keep their places) ──

    [DllImport("dwrite.dll")] private static extern int DWriteCreateFactory(int type, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")] private static extern IntPtr GetCurrentObject(IntPtr hdc, uint type);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr obj, int size, out DIBSECTION dib);
    private const uint Blackness = 0x00000042;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWRITE_FONT_METRICS
    {
        public ushort designUnitsPerEm, ascent, descent; public short lineGap; public ushort capHeight, xHeight; public short underlinePosition;
        public ushort underlineThickness; public short strikethroughPosition; public ushort strikethroughThickness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWRITE_GLYPH_METRICS { public int leftSideBearing; public uint advanceWidth; public int rightSideBearing, topSideBearing; public uint advanceHeight; public int bottomSideBearing, verticalOriginY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWRITE_GLYPH_RUN
    {
        public IntPtr fontFace; public float fontEmSize; public uint glyphCount; public IntPtr glyphIndices, glyphAdvances, glyphOffsets; public int isSideways; public uint bidiLevel;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        void GetSystemFontCollection(out IDWriteFontCollection collection, [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
        void _CreateCustomFontCollection();
        void _RegisterFontCollectionLoader();
        void _UnregisterFontCollectionLoader();
        void _CreateFontFileReference();
        void _CreateCustomFontFileReference();
        void _CreateFontFace();
        void _CreateRenderingParams();
        void _CreateMonitorRenderingParams();
        void CreateCustomRenderingParams(float gamma, float enhancedContrast, float clearTypeLevel, int pixelGeometry, int renderingMode, out IDWriteRenderingParams renderingParams);
        void _RegisterFontFileLoader();
        void _UnregisterFontFileLoader();
        void _CreateTextFormat();
        void _CreateTypography();
        void GetGdiInterop(out IDWriteGdiInterop interop);
    }

    [ComImport, Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig] uint GetFontFamilyCount();
        void GetFontFamily(uint index, out IDWriteFontFamily family);
        void FindFamilyName([MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, [MarshalAs(UnmanagedType.Bool)] out bool exists);
    }

    [ComImport, Guid("da20d8ef-812a-4c43-9802-62ec4abd7add"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        void _GetFontCollection();
        [PreserveSig] uint _GetFontCount();
        void _GetFont();
        void _GetFamilyNames();
        void GetFirstMatchingFont(int weight, int stretch, int style, out IDWriteFont font);
    }

    [ComImport, Guid("acd16696-8c14-4f5d-877e-fe3fc1d32737"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFont
    {
        void _GetFontFamily();
        [PreserveSig] int _GetWeight();
        [PreserveSig] int _GetStretch();
        [PreserveSig] int _GetStyle();
        [PreserveSig] int _IsSymbolFont();
        void _GetFaceNames();
        void _GetInformationalStrings();
        [PreserveSig] int _GetSimulations();
        [PreserveSig] void _GetMetrics(IntPtr metrics);
        void _HasCharacter();
        void CreateFontFace(out IDWriteFontFace face);
    }

    [ComImport, Guid("5f49804d-7024-4d43-bfa9-d25984f53849"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IDWriteFontFace
    {
        [PreserveSig] int _GetType();
        void _GetFiles();
        [PreserveSig] uint _GetIndex();
        [PreserveSig] int _GetSimulations();
        [PreserveSig] int _IsSymbolFont();
        [PreserveSig] void GetMetrics(out DWRITE_FONT_METRICS metrics);
        [PreserveSig] ushort _GetGlyphCount();
        void GetDesignGlyphMetrics(ushort* glyphIndices, uint count, DWRITE_GLYPH_METRICS* metrics, [MarshalAs(UnmanagedType.Bool)] bool isSideways);
        void GetGlyphIndices(uint* codePoints, uint count, ushort* glyphIndices);
    }

    [ComImport, Guid("1edd9491-9853-4299-898f-6432983b6f3a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteGdiInterop
    {
        void _CreateFontFromLOGFONT();
        void _ConvertFontToLOGFONT();
        void _ConvertFontFaceToLOGFONT();
        void _CreateFontFaceFromHdc();
        void CreateBitmapRenderTarget(IntPtr hdc, uint width, uint height, out IDWriteBitmapRenderTarget target);
    }

    [ComImport, Guid("5e5a32a3-8dff-4773-9ff6-0696eab77267"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteBitmapRenderTarget
    {
        void DrawGlyphRun(float baselineX, float baselineY, int measuringMode, ref DWRITE_GLYPH_RUN glyphRun, IDWriteRenderingParams renderingParams, uint textColor, out RECT blackBox);
        [PreserveSig] IntPtr GetMemoryDC();
        [PreserveSig] float _GetPixelsPerDip();
        void SetPixelsPerDip(float pixelsPerDip);
        void _GetCurrentTransform();
        void _SetCurrentTransform();
        void _GetSize();
        void Resize(uint width, uint height);
    }

    [ComImport, Guid("2f0da53a-2add-47cd-82ee-d9ec34688e75"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteRenderingParams { }
}
