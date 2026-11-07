using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeons hub — list thumbnails and the detail art band, from Dungeon Confirm's sources and helpers:
//   • hero dungeons: ChallengeHeroDungeonTable.Background of the shown difficulty (list thumb = the Master row's when the
//     group has one, else the Normal row's — v5); Unstable entries: NormalHeroDungeonTable.Background;
//   • raids: RaidDungeonTable.MonsterShowTexture (a TRANSPARENT boss cut-out) composed over its DiffBgTexture —
//     DcComposeRt (Plugin.DungeonConfirm.ArtCompose.cs), cached per (backdrop, cut-out) pair until the hub hides;
//   • activities: the recommended-play BackGroundPic (SeasonActTable) or ActivityDungeonTable.Background; World Boss:
//     this week's WorldBossSwitchTable.RecommendMainPic (→ MainPic) once the server's boss info is in (DhArtOf).
// Loaded with IGameAssets.LoadByPath (null while loading → the box shows its empty tile, no layout jump) and cropped
// to the box's aspect with DcCrop (raid: the cut-out's opaque band keeps the bosses' heads in).
public sealed partial class Plugin
{
    private readonly UvRect[] _dhThumbUv = new UvRect[DhListSlots];
    private readonly string[] _dhThumbKey = new string[DhListSlots];
    private UvRect _dhBandUv = new(0f, 0f, 1f, 1f);
    private string _dhBandKey = "";

    private sealed class DhComp { public UE.RenderTexture? Rt; public float Top, Bot; public bool Failed; }
    private readonly Dictionary<string, DhComp> _dhComp = new();

    private HudElement DhThumb(int i) => new CellElement(new BubbleElement(
        new GameTextureElement(() => DhThumbTex(i), (int)DhThumbW, (int)DhThumbH, () => _dhThumbUv[i])
            { Fill = true, CornerRadius = (int)DcS(5f) })
    {
        FillColorFn = () => DcTile(), CornerRadius = DcS(5f), PaddingX = 0f, PaddingY = 0f, MaxWidth = DhThumbW,
    }, Width: DhThumbW);

    private HudElement DhArtBand() => new BubbleElement(
        new GameTextureElement(DhBandTex, (int)DhDetInW, (int)DhArtH, () => _dhBandUv) { Fill = true, CornerRadius = (int)DhRadius })
    {
        FillColorFn = () => DcTile(), CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private object? DhThumbTex(int i)
    {
        var e = DhAt(i);
        if (e == null) return null;
        _dhThumbKey[i] ??= "";
        return DhTex(DhArtOf(e), e.ArtBg, DhThumbW / DhThumbH, ref _dhThumbKey[i], ref _dhThumbUv[i]);
    }

    private object? DhBandTex()
    {
        var e = DhCur;
        if (e == null) return null;
        string art = DhArtOf(e), bg = e.ArtBg;
        if (!e.IsActivity)
        {
            if (DhIsMaster) { if (e.MasterArt.Length > 0) { art = e.MasterArt; bg = ""; } }
            else if (DhCurLevel is { Art.Length: > 0 } l) { art = l.Art; bg = l.ArtBg; }
        }
        return DhTex(art, bg, DhDetInW / DhArtH, ref _dhBandKey, ref _dhBandUv);
    }

    private object? DhTex(string art, string bg, float aspect, ref string cropKey, ref UvRect cropUv)
    {
        if (art.Length == 0) return null;
        object? tex; UvRect uv; float top = DcArtFocus, bot = DcArtFocus;
        if (bg.Length > 0) tex = DhRaidTex(bg, art, out uv, out top, out bot);
        else tex = _services.GameAssets.LoadByPath(art, out uv);
        if (tex == null) return null;
        string key = $"{art}|{bg}|{(tex is UE.RenderTexture ? 'c' : 's')}|{top}|{bot}|{aspect}";
        if (key != cropKey) { cropKey = key; cropUv = DcCrop(tex, uv, aspect, top, bot); }
        return tex;
    }

    // The composed raid art for a pair, else the backdrop alone (while the cut-out loads, or if composing failed).
    private object? DhRaidTex(string bgPath, string cutPath, out UvRect uv, out float top, out float bot)
    {
        uv = new UvRect(0f, 0f, 1f, 1f); top = bot = DcArtFocus;
        string key = bgPath + "|" + cutPath;
        if (_dhComp.TryGetValue(key, out DhComp? c))
        {
            bool alive = false;
            try { alive = c.Rt != null && c.Rt.IsCreated(); } catch { }
            if (alive) { top = c.Top; bot = c.Bot; return c.Rt; }
            if (c.Rt != null) { DhReleaseRt(c.Rt); _dhComp.Remove(key); c = null; }   // lost (device reset) → compose again
        }
        var bgObj = _services.GameAssets.LoadByPath(bgPath, out var bgUv);
        if (c is not { Failed: true })
        {
            var cutObj = _services.GameAssets.LoadByPath(cutPath, out var cutUv);
            if (bgObj != null && cutObj != null)
            {
                var rt = DcComposeRt(bgObj, bgUv, cutObj, cutUv, "DungeonHub", out var t, out var b);
                _dhComp[key] = new DhComp { Rt = rt, Top = t, Bot = b, Failed = rt == null };
                if (rt != null) { top = t; bot = b; return rt; }
            }
        }
        if (bgObj != null) { uv = bgUv; return bgObj; }
        return null;
    }

    private static void DhReleaseRt(UE.RenderTexture rt) { try { rt.Release(); UE.Object.Destroy(rt); } catch { } }

    private void DhReleaseArt()
    {
        foreach (var c in _dhComp.Values) if (c.Rt != null) DhReleaseRt(c.Rt);
        _dhComp.Clear();
        _dhBandKey = "";
        for (int i = 0; i < _dhThumbKey.Length; i++) _dhThumbKey[i] = "";
    }
}
