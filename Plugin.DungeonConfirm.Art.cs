using System;
using System.IO;
using System.IO.Compression;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — art, icons and the AFFIXES panel.
//   • Art strip: the path the Lua poll picked (ChallengeHeroDungeon → RaidDungeon → TeamTarget → MatchAct → default),
//     loaded with IGameAssets.LoadByPath. GameTextureElement { Fill = true } STRETCHES the uv rect into the box, so to
//     fill the strip without distortion we hand it a uv CROP with the strip's aspect: full width, height cut to fit,
//     window placed by a vertical focus band (DcCrop; raids: the cut-out's opaque rows). Needs the texture's pixel size — read once
//     per path from the loaded UnityEngine.Texture (main thread: the Uv/Texture Funcs run on the render tick).
//   • Class icons: IGameAssets.LoadProfessionIcon(id, out uv), one uv slot per on-screen row (EntityInspector Party's
//     pattern). ⚠️ Nameplate-HUD.md warns about a shared per-profession load slot raced by two plugins; EI uses this same
//     call alongside CombatMeter today (validated 11-04), so we follow it — watch for blank icons in-game.
//   • Header icon (white crossed swords on the accent tile, white ✕ on an end state), the raid leader ★ and every ✓ / ✕
//     mark are generated PNGs (no art pipeline, no unverified glyphs). ImageElement can't tint, so colours are baked.
public sealed partial class Plugin
{
    // ── art strip ─────────────────────────────────────────────────────────────────────────────────────────────────
    private UvRect _dcArtUv = new(0f, 0f, 1f, 1f);
    private string _dcArtCropKey = "";

    private object? DcArtTex()
    {
        var path = _dc?.Art;
        if (string.IsNullOrEmpty(path)) return null;
        object? tex; UvRect uv; float top = DcArtFocus, bot = DcArtFocus;
        // A raid's art is a transparent cut-out with a separate backdrop → the composed RT (.ArtCompose.cs).
        if (_dc!.ArtBg.Length > 0) tex = DcRaidArt(_dc.ArtBg, path, out uv, out top, out bot);
        else tex = _services.GameAssets.LoadByPath(path, out uv);
        if (tex == null) return null;
        bool raid = _dc.Twenty;
        // Keyed on the texture KIND too: the raid strip can swap from a single-image fallback to the composed RT.
        string key = $"{path}|{_dc.ArtBg}|{(raid ? 'r' : '5')}|{(tex is UnityEngine.RenderTexture ? 'c' : 's')}|{top}|{bot}";
        if (key != _dcArtCropKey)
        {
            _dcArtCropKey = key;
            float boxW = (raid ? DcRW : Dc5W) - DcPad, boxH = raid ? DcRArtH : Dc5ArtH;
            _dcArtUv = DcCrop(tex, uv, boxW / boxH, top, bot);
        }
        return tex;
    }

    // Crop `uv` (bottom-left origin) to aspect `box` = w/h. Wider-than-box art keeps full height and is centred
    // horizontally; taller art keeps full width and its window follows a vertical FOCUS BAND [top, bot] (fractions from
    // the top): a band that fits is centred in the window; a taller one puts the window's top just above the band's top,
    // so the bosses' HEADS stay in (the composed raid art: band = the cut-out's opaque rows, 2026-11-06 — a fixed 40 %
    // focus cut the heads off). Chaotic Realm key art passes the point band DcArtFocus (centred 40 % from the top).
    private const float DcArtFocus = 0.40f, DcArtHeadroom = 0.03f;

    private static UvRect DcCrop(object tex, UvRect uv, float box, float top, float bot)
    {
        float pw, ph;
        try
        {
            if (tex is not UnityEngine.Texture t || t.width <= 0 || t.height <= 0) return uv;
            pw = t.width * uv.W; ph = t.height * uv.H;
        }
        catch { return uv; }
        float art = pw / ph;
        if (art > box)
        {
            float fw = box / art;   // fraction of the width kept
            return new UvRect(uv.X + uv.W * (1f - fw) / 2f, uv.Y, uv.W * fw, uv.H);
        }
        float fh = art / box;       // fraction of the height kept
        float want = bot - top <= fh ? (top + bot) / 2f - fh / 2f : top - DcArtHeadroom;
        float topFromTop = Math.Clamp(want, 0f, 1f - fh);
        return new UvRect(uv.X, uv.Y + uv.H * (1f - topFromTop - fh), uv.W, uv.H * fh);
    }

    // ── class icons: 25 uv slots (0..4 the 5-player rows, 5..24 the raid cells) ──────────────────────────────────
    private readonly UvRect[] _dcProfUv = new UvRect[25];

    // The white class line-art on a role-coloured badge, as MinimalNameplate draws it (ClassIconOverlay.cs ClassColor /
    // HudPoc AppendHudBadge): a ROUNDED SQUARE (radius 22 % of the side), icon 72 % of the side, colours copied exactly.
    // Badge = the old 32 px icon slot (so every row width stays exact), corner 7; icon 24 (72 % ≈ 23, rounded to even)
    // centred by two 4 px struts. Bots/AI get their profession's colour too; an Empty row has no badge (DcEmptyRow).
    private HudElement DcClassIcon(Func<DcMember?> m, int slot)
    {
        float side = (DcClassW - DcClassIconW) / 2f;
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(side, DcClassW),
            new GameTextureElement(
                () => m() is { Prof: > 0 } mm ? _services.GameAssets.LoadProfessionIcon(mm.Prof, out _dcProfUv[slot]) : null,
                (int)DcClassIconW, (int)DcClassIconW, () => _dcProfUv[slot]) { Fill = true },
            new SpacerElement(side, DcClassW),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(DcRoleColor(m()?.Prof ?? 0)), CornerRadius = DcBadgeRadius, PaddingX = 0f, PaddingY = 0f,
            MaxWidth = DcClassW,
        }, Width: DcClassW);
    }

    // MinimalNameplate ClassColor: 1/2/3/4/11 red · 5/13 green · 9/12 blue · else grey, all at 0.95 alpha.
    private static ColorRgba DcRoleColor(int prof) => prof switch
    {
        1 or 2 or 3 or 4 or 11 => new ColorRgba(0.74f, 0.16f, 0.16f, 0.95f),
        5 or 13                => new ColorRgba(0.16f, 0.58f, 0.27f, 0.95f),
        9 or 12                => new ColorRgba(0.18f, 0.40f, 0.82f, 0.95f),
        _                      => new ColorRgba(0.44f, 0.44f, 0.44f, 0.95f),
    };

    // ── header icon tile (64 px, the scaled ToolIconSize 40): accent + white swords; Danger red + white ✕ on an end state ─
    // The end-state ✕ is a generated PNG too: as BOLD text (32 px) it drew NOTHING in game (2026-11-06) — the TMP bold
    // face lacks U+2715 (same family as "■"/"▾", Stellar-Design-Language.md §6). Same 38 px box as the swords.
    private HudElement DcIconTile()
    {
        float side = (DcIconBox - DcSwordsW) / 2f;   // 13 + swords 38 + 13
        return new CellElement(new BubbleElement(
            new ConditionalElement(() => _dcEnd != DcEnd.None,
                new RowElement(new HudElement[]
                {
                    new SpacerElement(side, DcIconBox), new ImageElement(DcEndCrossPng, (int)DcSwordsW, (int)DcSwordsW),
                    new SpacerElement(side, DcIconBox),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                new RowElement(new HudElement[]
                {
                    new SpacerElement(side, DcIconBox), new ImageElement(DcSwordsPng, (int)DcSwordsW, (int)DcSwordsW),
                    new SpacerElement(side, DcIconBox),
                }, Gap: 0.01f, Justify: RowJustify.Center)))
            {
                FillColorFn = () => Fade(_dcEnd != DcEnd.None ? DangerFill : Menu.MenuAccent),
                CornerRadius = DcIconRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DcIconBox,
            }, Width: DcIconBox);
    }

    // ── AFFIXES panel (5-player): amber chips with the game's affix icon, + the key chip ──────────────────────────
    private const int DcMaxAffixes = 4;
    private readonly UvRect[] _dcAffixUv = new UvRect[DcMaxAffixes];

    private DcAffix? DcAffixAt(int i) => _dc != null && i < _dc.Affixes.Count ? _dc.Affixes[i] : null;

    private HudElement DcAffixPanel()
    {
        var chips = new HudElement[DcMaxAffixes + 1];
        for (int i = 0; i < DcMaxAffixes; i++)
        {
            int k = i;
            chips[i] = new ConditionalElement(() => DcAffixAt(k) != null, DcChip(
                new GameTextureElement(() => DcAffixAt(k) is { Icon.Length: > 0 } a
                        ? _services.GameAssets.LoadByPath(a.Icon, out _dcAffixUv[k]) : null,
                    (int)DcAffixIconW, (int)DcAffixIconW, () => _dcAffixUv[k]) { Fill = true, CornerRadius = (int)(DcAffixIconW / 2f) },
                () => DcAffixAt(k)?.Label ?? "", () => DcAmber));
        }
        chips[DcMaxAffixes] = new ConditionalElement(() => _dc is { KeyLeft: >= 0 },
            DcChip(null, () => LF("dc.key", _dc?.KeyLeft ?? 0), () => Menu.MenuAccent));
        return DcPanel(() => L("dc.cap.affixes"), DcSmall(() => "", MutedColor),   // hint left empty: the difficulty is the header chip
            new RowElement(chips, Gap: DcChipGap));
    }

    // 38 px chip (R1 radius 18) on a 22 % wash of its colour, bold 19 label; the 28 px affix icon is a full circle (as
    // the prototype's 18 / 9). Icon chips start 5 px in, text-only 14 (3 / 9 × DcScale); DcChipChrome must match.
    private HudElement DcChip(HudElement? icon, Func<string> label, Func<ColorRgba> color)
    {
        Func<ColorRgba> fill = () => DcWash(color(), 0.22f);
        var parts = icon != null
            ? new HudElement[] { new SpacerElement(DcS(3f), DcChipH), icon, new SpacerElement(DcS(5f), 0f),
                                 DcBold(label, () => EnsureContrast(color(), fill(), 4.5f), DcFsSmall), new SpacerElement(DcS(9f), 0f) }
            : new HudElement[] { new SpacerElement(DcS(9f), DcChipH),
                                 DcBold(label, () => EnsureContrast(color(), fill(), 4.5f), DcFsSmall), new SpacerElement(DcS(9f), 0f) };
        // ⚠️ MaxWidth is load-bearing. With MaxWidth 0 a bubble caps at its PARENT's laid-out width — here the chip's own
        // Conditional arm, whose width is just the bubble's last pinned width. Once the chip was pinned narrow (≈ icon
        // chrome, label not yet measured — the pre-built window builds and warms these lazily, off-screen), every later
        // re-measure was capped at that same width and the chip could never grow: label spilled under the next chip.
        // A fixed cap (the whole row) lets it hug its label again; the labels are pre-cut to fit (DcFitAffixLabels).
        return new BubbleElement(new RowElement(parts, Gap: 0.01f))
        {
            FillColorFn = () => Fade(fill()), CornerRadius = DcChipH / 2f - 1f, PaddingX = 0f, PaddingY = 0f,
            MaxWidth = DcAffixRowW,
        };
    }

    // ── generated glyph PNGs (2× the shown size, so they stay crisp) ──────────────────────────────────────────────
    // Each shape is defined on its own design grid (swords 48 units, star 24) and DcRaster samples it at any pixel size:
    // swords shown 38 → rendered 76 px, star shown 20 → rendered 40 px. The leader ★ is baked in the bright amber ink
    // #ffd27f (DcAmberInk, .Status.cs — style A, on the 22 % amber wash).
    private static byte[]? _dcSwords, _dcStar;

    private static byte[]? DcSwordsPng() => _dcSwords ??= DcRaster(2 * (int)DcSwordsW, 48f, 255, 255, 255, (x, y) =>
    {
        // Two mirrored swords on the 48-unit grid: blade, cross-guard, grip, pommel.
        static bool Sword(float x, float y) =>
            DcSeg(x, y, 15f, 33f, 40f, 8f, 2.6f) || DcSeg(x, y, 10.5f, 28.5f, 19.5f, 37.5f, 2.2f) ||
            DcSeg(x, y, 15f, 33f, 9f, 39f, 2.2f) || DcDist(x, y, 7.5f, 40.5f) <= 3.2f;
        return Sword(x, y) || Sword(48f - x, y);
    });

    private static byte[]? DcStarPng() => _dcStar ??= DcRaster(2 * (int)DcStarW, 24f, 0xff, 0xd2, 0x7f, (x, y) =>
    {
        // 5-point star, centre (12, 12.8), outer radius 11, inner 4.6 (even-odd point-in-polygon).
        bool inside = false;
        for (int i = 0, j = 9; i < 10; j = i++)
        {
            DcStarPt(i, out var xi, out var yi); DcStarPt(j, out var xj, out var yj);
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    });

    // End-state ✕ (white, shown 38 → 76 px): two round-capped diagonals on the 24 grid, ink ≈ 29 of 38 px — the
    // diagonal reads as big as the swords' 32 px ink.
    private static byte[]? _dcEndCross;
    private static byte[]? DcEndCrossPng() => _dcEndCross ??= DcRaster(2 * (int)DcSwordsW, 24f, 255, 255, 255, (x, y) =>
        DcSeg(x, y, 4.5f, 4.5f, 19.5f, 19.5f, 1.6f) || DcSeg(x, y, 19.5f, 4.5f, 4.5f, 19.5f, 1.6f));

    // ✓ / ✕ marks for the raid round marks and the "Confirmed" pill (2× the shown size). They were BOLD text, the
    // face that drew the header ✕ blank, so none of them is trusted as a glyph. ImageElement can't tint and is decoded
    // ONCE at build, so the colour is baked from the Func the builder pulls (style A ink / white at that moment);
    // cached per (shape, size, colour) so every row shares one byte[] (the builder dedups decodes by reference).
    // Gear: the hub header's settings button (icon-only, beside the ✕ — .DungeonHub.Window.cs DhSettingsButton).
    // Play: the settings' ▶ preview buttons (.DungeonConfirm.Settings.cs) — "▶" isn't trusted in the TMP faces either.
    // Chevron: the ▾ of the disabled sound-dropdown stand-in (.DungeonConfirm.Settings.cs DsDropdownOff), 8 wide × 5 tall ink.
    // Stop: the ■ a playing ▶ preview turns into — a rounded square 16 of 24 (8 of 12 shown); "■" draws blank in bold TMP.
    private enum DcGlyph { Check, Cross, Gear, Play, Chevron, Stop }
    private static readonly System.Collections.Generic.Dictionary<(DcGlyph, int, int), byte[]?> _dcGlyphs = new();

    private static byte[]? DcGlyphPng(DcGlyph g, float shown, ColorRgba c)
    {
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        byte r = B(c.R), gr = B(c.G), b = B(c.B);
        var key = (g, (int)shown, (r << 16) | (gr << 8) | b);
        if (_dcGlyphs.TryGetValue(key, out var png)) return png;
        // 24 grid, stroke radius 2.3 (≈ a bold stroke at 14–16 px shown).
        Func<float, float, bool> inside = g switch
        {
            DcGlyph.Check => (x, y) => DcSeg(x, y, 4.5f, 12.5f, 9.5f, 17.5f, 2.3f) || DcSeg(x, y, 9.5f, 17.5f, 19.5f, 6.5f, 2.3f),
            DcGlyph.Gear  => DcGearInside,
            DcGlyph.Play  => (x, y) => x >= 7.5f && x <= 19.5f && MathF.Abs(y - 12f) <= (19.5f - x) * 0.6f,
            DcGlyph.Chevron => (x, y) => y >= 4.5f && y <= 19.5f && MathF.Abs(x - 12f) <= (19.5f - y) * 0.8f,
            DcGlyph.Stop  => (x, y) => DcSeg(x, y, 6.5f, 6.5f, 17.5f, 6.5f, 2.5f) || DcSeg(x, y, 6.5f, 17.5f, 17.5f, 17.5f, 2.5f) ||
                                       (x >= 4f && x <= 20f && y >= 6.5f && y <= 17.5f) || (x >= 6.5f && x <= 17.5f && y >= 4f && y <= 20f),
            _             => (x, y) => DcSeg(x, y, 6f, 6f, 18f, 18f, 2.3f) || DcSeg(x, y, 18f, 6f, 6f, 18f, 2.3f),
        };
        return _dcGlyphs[key] = DcRaster(2 * (int)shown, 24f, r, gr, b, inside);
    }

    // "Confirmed ✓" footer pill (team + match mode, after I confirm): not clickable, but a SOLID green pill, not the
    // greyed disabled look (user, 2026-11-06), same 186×44 slot so nothing moves. The ✓ is a generated image AFTER the
    // word, the pair centred. WHITE text + ✓ on a deeper green #22a35a (≈ 3.3:1) — white on StatusOn #5fd38d was only
    // ~1.9:1, and the dark-ink fix (0f3320 held by EnsureContrast) read wrong in game; no contrast walk here. The fill is
    // theme-independent, so the PNG colour baked once at build is always right.
    //
    // Centring: the zero-height spacer pins the Column (and so the stretched Row) to the pill's inner width — without it
    // the bubble would hug the narrower content. It is a Column SIBLING of the Row, so it never takes row space; the Row
    // centres only because DcDrop is non-flexible (its Cell note, .Window.cs) — every child must stay natural-width or it
    // eats the slack and the pair goes left.
    private static readonly ColorRgba DcConfirmedFill = Rgb(0x22a35a), DcConfirmedInk = Rgb(0xffffff);
    private static ColorRgba DcConfirmedText() => DcConfirmedInk;

    private HudElement DcConfirmedPill() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(DcPillPadX, DcPillH),
        new ColumnElement(new HudElement[]
        {
            new SpacerElement(DcPillW - 2f * DcPillPadX, 0f),   // pins the width (centring note above)
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DcPillH), DcDrop(DcBold(() => L("dc.btn.confirmed"), DcConfirmedText, DcFsBody)),
                new SpacerElement(DcS(3f), 0f),
                new ImageElement(() => DcGlyphPng(DcGlyph.Check, DcGlyphLW, DcConfirmedText()), (int)DcGlyphLW, (int)DcGlyphLW),
            }, Gap: 0.01f, Justify: RowJustify.Center),
        }, Gap: 0.01f),
        new SpacerElement(DcPillPadX, DcPillH),
    }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(DcConfirmedFill), CornerRadius = DcPillH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DcPillW,
    };

    private static void DcStarPt(int i, out float x, out float y)
    {
        double a = -Math.PI / 2 + i * Math.PI / 5;
        float r = i % 2 == 0 ? 11f : 4.6f;
        x = 12f + r * (float)Math.Cos(a); y = 12.8f + r * (float)Math.Sin(a);
    }

    // Gear on the 24 grid: a ring (r 3.2–7.4 around the centre) + 8 square teeth (out to r 10.4, 3.8 wide), the hole
    // left open so it reads as a gear, not a blob, at 19 px shown.
    private static bool DcGearInside(float x, float y)
    {
        float dx = x - 12f, dy = y - 12f, d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < 3.2f || d > 10.4f) return false;
        if (d <= 7.4f) return true;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f, c = MathF.Cos(a), s = MathF.Sin(a);
            if (MathF.Abs(-dx * s + dy * c) <= 1.9f && dx * c + dy * s >= 0f) return true;
        }
        return false;
    }

    private static float DcDist(float x, float y, float cx, float cy) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

    // Inside the capsule around segment (ax,ay)-(bx,by) with radius r.
    private static bool DcSeg(float x, float y, float ax, float ay, float bx, float by, float r)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
        return DcDist(x, y, ax + t * dx, ay + t * dy) <= r;
    }

    // size×size RGBA, colour (r,g,b), alpha = 4×4 supersampled coverage of `inside`, which is evaluated in the shape's
    // own `grid`-unit space (y down, image space) — so the same shape renders crisply at any pixel size.
    private static byte[]? DcRaster(int size, float grid, byte r, byte g, byte b, Func<float, float, bool> inside)
    {
        try
        {
            var px = new byte[size * size * 4];
            float k = grid / size;   // design units per pixel
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < 4; sy++)
                for (int sx = 0; sx < 4; sx++)
                    if (inside((x + (sx + 0.5f) / 4f) * k, (y + (sy + 0.5f) / 4f) * k)) hit++;
                int o = (y * size + x) * 4;
                px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = (byte)(hit * 255 / 16);
            }
            return DcEncodePng(size, size, px);
        }
        catch { return null; }
    }

    // Minimal PNG writer: 8-bit RGBA, filter 0 on every row, zlib IDAT.
    private static byte[] DcEncodePng(int w, int h, byte[] rgba)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        DcBe(ihdr, 0, w); DcBe(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        DcChunk(png, "IHDR", ihdr);
        var raw = new byte[h * (w * 4 + 1)];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            DcChunk(png, "IDAT", z.ToArray());
        }
        DcChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void DcBe(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void DcChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; DcBe(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4]; DcBe(crc, 0, (int)DcCrc(td)); s.Write(crc);
    }

    private static uint DcCrc(byte[] d)
    {
        uint c = 0xffffffffu;
        foreach (var x in d)
        {
            c ^= x;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xffffffffu;
    }
}
