using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeons hub — NPC tab art (user-approved "option B", 2026-11-21; scenery + gradient thumb 2026-11-21). No NPC portrait
// exists (Dungeon-Access-Hub.md §13 "NPC art"), so each NPC carries game paths on its N record (.Npc.cs ART / MapIcon):
//   • banner (detail art band) = a SCENERY backdrop (Mael: the Lallipar village panorama, a MapStickerTable scenic-spot
//     photo) cropped to the band, darkened, with the NPC's emblem (Mael: the Matrix page art, then the season emblem)
//     drawn WHOLE on top — one RenderTexture composed once per open (.NpcCompose.cs). No backdrop, or it did not load:
//     the emblem alone as before — a WIDE image (pixel aspect ≥ DhNpcFitBelow) fills the band like every other entry
//     (DcCrop); a square / tall one would lose most of itself to that crop, so it is drawn whole on the band's tile.
//   • thumb (list row) = a MultiTool-style gradient tile picked from the NPC id, with the NPC's map marker aspect-fit
//     on it (.NpcCompose.cs); no marker (or it failed) = the gradient alone.
// Aspect-fit needs no extra element: the art GameTextureElements are Fill = false (letterbox, .Art.cs), and a uv cropped
// to the box's aspect letterboxes to exactly the box — so "fill" = DcCrop'd uv, "fit" = the image's own uv. A composed
// RT is made at the box's aspect, so its full uv fills the box.
// IGameAssets.LoadByPath has no failure signal (null while loading AND on failure), so a candidate path that is still
// null DhNpcArtWaitSec after it was really requested counts as failed → the next one; none left → the fallback above.
// While a piece is still within its wait, the box shows its empty tile (never a half-built look that swaps a frame later).
// Each path goes through DhLoad (.ArtLoad.cs: per-frame request budget, the framework caches the asset); the pick +
// its display uv are worked out once and kept for the session. One log line per NPC once every piece has settled.
public sealed partial class Plugin
{
    private const float DhNpcFitBelow = 1.6f;      // banner pixel aspect below this = drawn whole (fit), else cropped
    private const double DhNpcArtWaitSec = 3.0;    // a requested path still null after this long = failed
    private static readonly UvRect DhFullUv = new(0f, 0f, 1f, 1f);

    private enum DhFit { ByShape, Fit, Crop }

    private sealed class DhArtPick
    {
        public string[] Paths = System.Array.Empty<string>();
        public DhFit Mode;
        public int At;                 // current candidate; At ≥ Paths.Length = none loaded (empty tile)
        public double Since = -1;      // TimeNow when Paths[At] was first really requested
        public bool Done;              // Paths[At] loaded; Uv/Raw/Fit/Size are set
        public bool Fit;
        public UvRect Uv;              // the display uv (fit: the image's own; fill / crop: cropped to the box)
        public UvRect Raw;             // the image's own uv (what a compose draws whole)
        public int W, H;               // the image's pixel footprint (for the log)
        public bool Settled => Done || At >= Paths.Length;
    }

    private sealed class DhNpcArt
    {
        public string Sig = "", SigThumb = "", SigBg = "";
        public DhArtPick Banner = new(), Thumb = new(), Backdrop = new();
        public int Palette;                       // DhGradPal index, from the NPC id (.NpcCompose.cs)
        public string? BandKey, ThumbKey;         // _dhComp keys, built once when their pieces have loaded
        public bool Logged, BandLogged, ThumbLogged;
    }
    private readonly Dictionary<string, DhNpcArt> _dhNpcArt = new();

    // Per entry key; rebuilt only when the entry's paths change (entries are new objects on every catalog parse, but the
    // NPC walk is cached per login on the Lua side, so the paths are the same strings each time).
    private DhNpcArt DhNpcArtOf(DhEntry e)
    {
        if (_dhNpcArt.TryGetValue(e.Key, out var a) && a.Sig == e.NpcBanner && a.SigThumb == e.NpcThumb && a.SigBg == e.NpcBackdrop)
            return a;
        int.TryParse(e.Key.Length > 1 ? e.Key.Substring(1) : "", out int npcId);   // key 'n<npcId>'
        a = new DhNpcArt
        {
            Sig = e.NpcBanner, SigThumb = e.NpcThumb, SigBg = e.NpcBackdrop,
            Banner = new DhArtPick { Paths = DhPaths(e.NpcBanner, e.NpcBannerFb), Mode = DhFit.ByShape },
            Thumb = new DhArtPick { Paths = DhPaths(e.NpcThumb, ""), Mode = DhFit.Fit },
            Backdrop = new DhArtPick { Paths = DhPaths(e.NpcBackdrop, ""), Mode = DhFit.Crop },
            Palette = DhGradIndex(npcId),
        };
        _dhNpcArt[e.Key] = a;
        return a;
    }

    private static string[] DhPaths(string a, string b) =>
        a.Length > 0 && b.Length > 0 && a != b ? new[] { a, b } : a.Length > 0 ? new[] { a } : b.Length > 0 ? new[] { b } : System.Array.Empty<string>();

    // Band: scenery + emblem composite, else the emblem alone with its display uv. cropKey is cleared so the next
    // non-NPC entry recomputes its crop.
    private object? DhNpcBandTex(DhEntry e, ref string cropKey, ref UvRect cropUv)
    {
        var a = DhNpcArtOf(e);
        cropKey = "";
        float box = DhDetInW / DhArtH;
        var emb = DhPickTex(e, a, a.Banner, box);
        var p = a.Backdrop;
        if (p.Paths.Length > 0 && !(p.Settled && !p.Done))   // a backdrop that has not failed
        {
            var bg = DhPickTex(e, a, p, box);
            if (bg == null || !a.Banner.Settled) return null;   // still loading (≤ DhNpcArtWaitSec): the empty tile
            if (emb == null) { cropUv = p.Uv; return bg; }      // no emblem: the scenery alone
            a.BandKey ??= "npcb|" + p.Paths[p.At] + "|" + a.Banner.Paths[a.Banner.At];
            var rt = DhNpcComposed(a.BandKey, out bool failed);
            if (rt == null && !failed)
            {
                // Same rules as the raid pairs (.Art.cs DhRaidTex): one compose per frame, never while hidden.
                if (!_dhShow || !DhComposeAllowed()) return null;
                rt = DhNpcComposeBand(bg, p.Uv, emb, a.Banner.Raw, box);
                DhNpcKeep(a.BandKey, rt);
                if (rt != null && !a.BandLogged) { a.BandLogged = true; _services.Log.Info($"[DungeonHub] npc {e.Key} banner composed {rt.width}x{rt.height}"); }
            }
            if (rt != null) { cropUv = DhFullUv; return rt; }
            // compose failed → the emblem alone (below)
        }
        if (emb != null) cropUv = a.Banner.Uv;
        return emb;
    }

    // Thumb: the NPC's gradient tile with its map marker on it; the gradient alone without a marker.
    private object? DhNpcThumbTex(DhEntry e, ref string cropKey, ref UvRect cropUv)
    {
        var a = DhNpcArtOf(e);
        cropKey = "";
        var icon = DhPickTex(e, a, a.Thumb, DhThumbW / DhThumbH);
        var grad = DhGradTex(a.Palette);
        if (grad == null) { if (icon != null) cropUv = a.Thumb.Uv; return icon; }   // generating failed: today's look
        if (icon == null)
        {
            if (!a.Thumb.Settled) return null;   // marker still loading: the empty tile
            cropUv = DhFullUv; return grad;      // no marker / it failed
        }
        a.ThumbKey ??= "npct|" + a.Palette + "|" + a.Thumb.Paths[a.Thumb.At];
        var rt = DhNpcComposed(a.ThumbKey, out bool failed);
        if (rt == null && !failed)
        {
            if (!_dhShow || !DhComposeAllowed()) return null;
            rt = DhNpcComposeThumb(grad, icon, a.Thumb.Raw);
            DhNpcKeep(a.ThumbKey, rt);
            if (rt != null && !a.ThumbLogged) { a.ThumbLogged = true; _services.Log.Info($"[DungeonHub] npc {e.Key} thumb composed {rt.width}x{rt.height} (palette {a.Palette})"); }
        }
        if (rt != null) { cropUv = DhFullUv; return rt; }
        cropUv = a.Thumb.Uv; return icon;   // compose failed: the marker alone, as before
    }

    private object? DhPickTex(DhEntry e, DhNpcArt a, DhArtPick p, float box)
    {
        if (p.At >= p.Paths.Length) { DhNpcArtLog(e, a); return null; }
        var path = p.Paths[p.At];
        var tex = DhLoad(path, out var uv);
        if (tex != null)
        {
            if (!p.Done) { DhPickResolve(p, tex, uv, box); DhNpcArtLog(e, a); }
            return tex;
        }
        if (p.Done) return null;   // was loaded once; null now = the framework dropped it (shows the tile, no fallback)
        if (!_dhArtAsked.Contains(path)) return null;   // not requested yet (DhLoad's per-frame budget): no clock
        double now = DcNow;
        if (p.Since < 0) p.Since = now;
        else if (now - p.Since >= DhNpcArtWaitSec) { p.At++; p.Since = -1; if (p.At >= p.Paths.Length) DhNpcArtLog(e, a); }
        return null;
    }

    private static void DhPickResolve(DhArtPick p, object tex, UvRect uv, float box)
    {
        p.Done = true;
        p.Raw = uv;
        float aspect = 0f;
        try
        {
            if (DcAsTex(tex) is { width: > 0, height: > 0 } t)
            {
                p.W = (int)(t.width * uv.W + 0.5f); p.H = (int)(t.height * uv.H + 0.5f);
                if (p.H > 0) aspect = (float)p.W / p.H;
            }
        }
        catch { }
        // Unknown size → fit (the letterbox shows it whole either way; a blind crop could cut the subject out).
        // A scenery backdrop is always cropped to the box (its vertical focus: DhNpcBgFocus, .NpcCompose.cs).
        p.Fit = p.Mode == DhFit.Fit || (p.Mode == DhFit.ByShape && (aspect <= 0f || aspect < DhNpcFitBelow));
        p.Uv = p.Fit ? uv : DcCrop(tex, uv, box, p.Mode == DhFit.Crop ? DhNpcBgFocus : DcArtFocus, p.Mode == DhFit.Crop ? DhNpcBgFocus : DcArtFocus);
    }

    // One line per NPC, once every piece has settled (loaded or out of candidates): path, pixel size, fallback, mode.
    private void DhNpcArtLog(DhEntry e, DhNpcArt a)
    {
        if (a.Logged || !a.Banner.Settled || !a.Thumb.Settled || !a.Backdrop.Settled) return;
        a.Logged = true;
        _services.Log.Info($"[DungeonHub] npc {e.Key} art: banner {DhPickNote(a.Banner)} · backdrop {DhPickNote(a.Backdrop)} · " +
            $"thumb {DhPickNote(a.Thumb)} on gradient {a.Palette}");
    }

    private static string DhPickNote(DhArtPick p)
    {
        if (p.Paths.Length == 0) return "none";
        if (!p.Done) return $"FAILED all {p.Paths.Length} ({string.Join(", ", p.Paths)})";
        var fb = p.At == 0 ? "" : $" [fallback {p.At}, '{p.Paths[0]}' did not load]";
        var mode = p.Mode == DhFit.Crop ? $"crop (focus {DhNpcBgFocus:0.00})" : p.Fit ? "fit (whole, centred)" : "fill (cropped)";
        return $"{p.Paths[p.At]} {p.W}x{p.H} {mode}{fb}";
    }
}
