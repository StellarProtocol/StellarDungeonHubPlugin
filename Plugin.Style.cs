using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the shared palette + colour maths
// + embedded-PNG cache, copied from StellarExperimentPlugin Plugin.Style.cs (itself from StellarMultiToolPlugin) and
// trimmed to what Dungeon Hub / Dungeon Confirm use. Their own scaled building blocks (DcBold / DcSmall / DcPill …) live
// in Plugin.DungeonConfirm.Window.cs and Plugin.DungeonHub.Window.cs.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Every surface/text colour is DERIVED from _services.Theme.Colors on each call and handed to elements as a Func, so
    // a theme switch repaints on the next refresh. Only SEMANTIC colours stay fixed (status green, danger red).
    private static readonly ColorRgba PanelShade  = new(0f, 0f, 0f, 0.20f);   // panel = background darkened

    private static readonly ColorRgba StatusOn    = Rgb(0x5fd38d);
    private static readonly ColorRgba ErrorText   = Rgb(0xff8a8b);
    private static readonly ColorRgba DangerFill  = Rgb(0xff4d4f, 0.85f);
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()    => Menu.MenuText;
    private ColorRgba MutedColor()   => Menu.MenuMuted;
    private ColorRgba PanelColor()   => Over(PanelShade, Menu.MenuBackground);

    // ── window opacity: large inner surfaces follow Themes → Window Opacity ───────────────────────────────────────
    // The framework fades ONLY the GlassMenu frame Image (IChromeStyle.WindowOpacity, WindowBuilder-Patterns.md
    // §"WindowSpec.FrameOpacity"); our panels / tiles / tab track / bottom bar are opaque pre-blended fills, so at a
    // low opacity they stayed solid on a see-through frame. Fade(c) multiplies the alpha of such a SURFACE fill by the
    // live value. The colour is still composed OPAQUE first (Over(…, MenuBackground / PanelColor())) and only then gets
    // its alpha — never composite over a transparent background. NOT faded (full alpha on purpose): text, images / art,
    // the accent icon tile, coloured action pills (Solo / Party / Match / Accept / Confirm / Cancel), selected-tab and
    // selected-row highlights and state chips.
    // IChromeStyle isn't on IPluginServices; the shared INamedTheme instance is the framework theme service, which
    // implements it too (ChatTools Plugin.WindowOpacity.cs). No cast → 1 (opaque, the old look). Polled per refresh: a
    // cast + a property read. Clamped to the framework's own 0.3–1 range.
    private float SurfaceAlpha()
    {
        try { return Math.Clamp((_services.NamedTheme as IChromeStyle)?.WindowOpacity ?? 1f, 0.3f, 1f); }
        catch { return 1f; }
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * SurfaceAlpha() };

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque bg
    // (ChatTools Plugin.Tabs.cs). Pure struct math — no allocation on the per-refresh colour funcs.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb)
        => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);
    private static ColorRgba Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.DungeonHub.<file>") and kept: ImageElement
    // Funcs are polled. A missing resource caches null, so callers fall back to no icon.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.DungeonHub." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }
}
