using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeons hub — NPC tab art (user-approved "option B", 2026-11-21). No NPC portrait exists (Dungeon-Access-Hub.md §13
// "NPC art"), so each NPC carries two game paths on its N record (.Npc.cs ART / NpcTable.MapIcon):
//   • banner (detail art band): e.g. Mael = the Matrix Manifestation page art, then a fallback (the season emblem).
//     A WIDE image (pixel aspect ≥ DhNpcFitBelow) fills the band like every other entry (DcCrop); a square / tall one
//     would lose most of itself to that crop, so it is drawn WHOLE, aspect-fit and centred on the band's tile.
//   • thumb (list row): the NPC's map marker, always aspect-fit in the thumbnail tile (never stretched or cropped).
// Aspect-fit needs no extra element: the art GameTextureElements are Fill = false (letterbox, .Art.cs), and a uv cropped
// to the box's aspect letterboxes to exactly the box — so "fill" = DcCrop'd uv, "fit" = the image's own uv.
// IGameAssets.LoadByPath has no failure signal (null while loading AND on failure), so a candidate path that is still
// null DhNpcArtWaitSec after it was really requested counts as failed → the next one; none left → the empty tile.
// Each path goes through DhLoad (.ArtLoad.cs: per-frame request budget, the framework caches the asset); the pick +
// its display uv are worked out once and kept for the session. One log line per NPC once both pieces have settled.
public sealed partial class Plugin
{
    private const float DhNpcFitBelow = 1.6f;      // banner pixel aspect below this = drawn whole (fit), else cropped
    private const double DhNpcArtWaitSec = 3.0;    // a requested path still null after this long = failed

    private sealed class DhArtPick
    {
        public string[] Paths = System.Array.Empty<string>();
        public int At;                 // current candidate; At ≥ Paths.Length = none loaded (empty tile)
        public double Since = -1;      // TimeNow when Paths[At] was first really requested
        public bool Done;              // Paths[At] loaded; Uv/Fit/Size are set
        public bool Fit;
        public UvRect Uv;              // the display uv (fit: the image's own; fill: cropped to the box)
        public int W, H;               // the image's pixel footprint (for the log)
        public bool Settled => Done || At >= Paths.Length;
    }

    private sealed class DhNpcArt { public string Sig = "", SigThumb = ""; public DhArtPick Banner = new(), Thumb = new(); public bool Logged; }
    private readonly Dictionary<string, DhNpcArt> _dhNpcArt = new();

    // Per entry key; rebuilt only when the entry's paths change (entries are new objects on every catalog parse, but the
    // NPC walk is cached per login on the Lua side, so the paths are the same strings each time).
    private DhNpcArt DhNpcArtOf(DhEntry e)
    {
        if (_dhNpcArt.TryGetValue(e.Key, out var a) && a.Sig == e.NpcBanner && a.SigThumb == e.NpcThumb) return a;
        a = new DhNpcArt
        {
            Sig = e.NpcBanner, SigThumb = e.NpcThumb,
            Banner = new DhArtPick { Paths = DhPaths(e.NpcBanner, e.NpcBannerFb) },
            Thumb = new DhArtPick { Paths = DhPaths(e.NpcThumb, "") },
        };
        _dhNpcArt[e.Key] = a;
        return a;
    }

    private static string[] DhPaths(string a, string b) =>
        a.Length > 0 && b.Length > 0 && a != b ? new[] { a, b } : a.Length > 0 ? new[] { a } : b.Length > 0 ? new[] { b } : System.Array.Empty<string>();

    // Band: the pick's texture with its display uv. cropKey is cleared so the next non-NPC entry recomputes its crop.
    private object? DhNpcBandTex(DhEntry e, ref string cropKey, ref UvRect cropUv)
    {
        var a = DhNpcArtOf(e);
        var tex = DhPickTex(e, a, a.Banner, DhDetInW / DhArtH, fitAlways: false);
        cropKey = "";
        if (tex != null) cropUv = a.Banner.Uv;
        return tex;
    }

    private object? DhNpcThumbTex(DhEntry e, ref string cropKey, ref UvRect cropUv)
    {
        var a = DhNpcArtOf(e);
        var tex = DhPickTex(e, a, a.Thumb, DhThumbW / DhThumbH, fitAlways: true);
        cropKey = "";
        if (tex != null) cropUv = a.Thumb.Uv;
        return tex;
    }

    private object? DhPickTex(DhEntry e, DhNpcArt a, DhArtPick p, float box, bool fitAlways)
    {
        if (p.At >= p.Paths.Length) { DhNpcArtLog(e, a); return null; }
        var path = p.Paths[p.At];
        var tex = DhLoad(path, out var uv);
        if (tex != null)
        {
            if (!p.Done) { DhPickResolve(p, tex, uv, box, fitAlways); DhNpcArtLog(e, a); }
            return tex;
        }
        if (p.Done) return null;   // was loaded once; null now = the framework dropped it (shows the tile, no fallback)
        if (!_dhArtAsked.Contains(path)) return null;   // not requested yet (DhLoad's per-frame budget): no clock
        double now = DcNow;
        if (p.Since < 0) p.Since = now;
        else if (now - p.Since >= DhNpcArtWaitSec) { p.At++; p.Since = -1; if (p.At >= p.Paths.Length) DhNpcArtLog(e, a); }
        return null;
    }

    private static void DhPickResolve(DhArtPick p, object tex, UvRect uv, float box, bool fitAlways)
    {
        p.Done = true;
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
        p.Fit = fitAlways || aspect <= 0f || aspect < DhNpcFitBelow;
        p.Uv = p.Fit ? uv : DcCrop(tex, uv, box, DcArtFocus, DcArtFocus);
    }

    // One line per NPC, once both pieces have settled (loaded or out of candidates): path, pixel size, fallback, mode.
    private void DhNpcArtLog(DhEntry e, DhNpcArt a)
    {
        if (a.Logged || !a.Banner.Settled || !a.Thumb.Settled) return;
        a.Logged = true;
        _services.Log.Info($"[DungeonHub] npc {e.Key} art: banner {DhPickNote(a.Banner, true)} · thumb {DhPickNote(a.Thumb, false)}");
    }

    private static string DhPickNote(DhArtPick p, bool band)
    {
        if (p.Paths.Length == 0) return "none (empty tile)";
        if (!p.Done) return $"FAILED all {p.Paths.Length} ({string.Join(", ", p.Paths)}) → empty tile";
        var fb = p.At == 0 ? "" : $" [fallback {p.At}, '{p.Paths[0]}' did not load]";
        var mode = band ? (p.Fit ? "fit (whole, centred)" : "fill (cropped)") : "fit";
        return $"{p.Paths[p.At]} {p.W}x{p.H} {mode}{fb}";
    }
}
