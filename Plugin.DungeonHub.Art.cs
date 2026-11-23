using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeons hub — list thumbnails and the detail art band, from Dungeon Confirm's sources and helpers:
//   • hero dungeons: ChallengeHeroDungeonTable.Background of the shown difficulty (list thumb = the Master row's when the
//     group has one, else its lowest level's: Normal, or Hard on a Season 4 group — v5); Unstable entries: NormalHeroDungeonTable.Background;
//   • raids: RaidDungeonTable.MonsterShowTexture (a TRANSPARENT boss cut-out) composed over its DiffBgTexture —
//     DcComposeRt (Plugin.DungeonConfirm.ArtCompose.cs), cached per (backdrop, cut-out) pair until logout (.ArtCache.cs);
//   • activities: the recommended-play BackGroundPic (SeasonActTable) or ActivityDungeonTable.Background; World Boss:
//     this week's WorldBossSwitchTable.RecommendMainPic (→ MainPic) once the server's boss info is in (DhArtOf).
// Loaded with IGameAssets.LoadByPath (null while loading → the box shows its empty tile, no layout jump) and cropped
// to the box's aspect with DcCrop (raid: the cut-out's opaque band keeps the bosses' heads in). New loads and raid
// composes are rationed per frame (DhLoad / DhComposeAllowed, .ArtLoad.cs) so an open never starts them all in one frame.
// Both boxes are Fill = false (the framework's aspect-fit letterbox): a uv cropped to the box's aspect fits it exactly,
// so cropped art looks as it did with Fill = true, and the NPC tab's square art can show WHOLE (.NpcArt.cs).
public sealed partial class Plugin
{
    private sealed class DhComp { public UE.RenderTexture? Rt; public float Top, Bot; public bool Failed; }
    private readonly Dictionary<string, DhComp> _dhComp = new();

    // i = the DATA index (the list's pool slot resolves it, .Window.cs); the crop is the entry's own (.ArtCache.cs).
    private HudElement DhThumb(Func<int> i) => new CellElement(new BubbleElement(
        new GameTextureElement(() => DhAt(i()) is { } e ? DhThumbTexOf(e) : null, (int)DhThumbW, (int)DhThumbH, () => DhThumbUvOf(DhAt(i())))
            { CornerRadius = (int)DcS(5f) })
    {
        FillColorFn = () => Fade(DcTile()), CornerRadius = DcS(5f), PaddingX = 0f, PaddingY = 0f, MaxWidth = DhThumbW,
    }, Width: DhThumbW);

    private HudElement DhArtBand() => new BubbleElement(
        new GameTextureElement(DhBandTex, (int)DhDetInW, (int)DhArtH, () => _dhBandCrop.Uv) { CornerRadius = (int)DhRadius })
    {
        FillColorFn = () => Fade(DcTile()), CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f,
    };

    // Also called by the warm-up (.Prewarm.cs) for every tab's entries, so their art is in before the first open.
    private object? DhThumbTexOf(DhEntry e)
    {
        var crop = DhThumbCropOf(e);
        if (e.IsNpc) return DhNpcThumbTex(e, crop);   // map marker, fit (.NpcArt.cs)
        return DhTex(DhArtOf(e), e.ArtBg, DhThumbW / DhThumbH, crop);
    }

    private object? DhBandTex()
    {
        var e = DhCur;
        if (e == null) return null;
        if (e.IsNpc) return DhNpcBandTex(e, _dhBandCrop);   // banner, fit or fill by its shape (.NpcArt.cs)
        string art = DhArtOf(e), bg = e.ArtBg;
        if (!e.IsActivity)
        {
            if (DhIsMaster) { if (e.MasterArt.Length > 0) { art = e.MasterArt; bg = ""; } }
            else if (DhCurLevel is { Art.Length: > 0 } l) { art = l.Art; bg = l.ArtBg; }
        }
        return DhTex(art, bg, DhDetInW / DhArtH, _dhBandCrop);
    }

    // Allocation-free on the per-frame path: the crop is redone only when its inputs change (.ArtCache.cs DhCropTo).
    private object? DhTex(string art, string bg, float aspect, DhCrop crop)
    {
        if (art.Length == 0) return null;
        object? tex; UvRect uv; float top = DcArtFocus, bot = DcArtFocus;
        if (bg.Length > 0) tex = DhRaidTex(bg, art, out uv, out top, out bot);
        else tex = DhLoad(art, out uv);   // per-frame request budget (.ArtLoad.cs)
        if (tex == null) return null;
        DhCropTo(crop, tex, uv, art, bg, aspect, top, bot);
        return tex;
    }

    // The composed raid art for a pair, else the backdrop alone (while the cut-out loads, or if composing failed).
    private object? DhRaidTex(string bgPath, string cutPath, out UvRect uv, out float top, out float bot)
    {
        uv = new UvRect(0f, 0f, 1f, 1f); top = bot = DcArtFocus;
        var key = (bgPath, cutPath);
        if (_dhRaidComp.TryGetValue(key, out DhComp? c))
        {
            bool alive = false;
            try { alive = c.Rt != null && c.Rt.IsCreated(); } catch { }
            if (alive) { top = c.Top; bot = c.Bot; return c.Rt; }
            if (c.Rt != null) { DhReleaseRt(c.Rt); _dhRaidComp.Remove(key); c = null; }   // lost (device reset) → compose again
        }
        var bgObj = DhLoad(bgPath, out var bgUv);
        if (c is not { Failed: true })
        {
            var cutObj = DhLoad(cutPath, out var cutUv);
            if (bgObj != null && cutObj != null)
            {
                // One compose per frame (.ArtLoad.cs): a deferred pair shows the empty tile one more frame, never the bare
                // backdrop (that would swap to the composed art a frame later = a visible flick).
                // Composing while hidden is fine (the warm-up and the off-screen Prebuild fill do it): RTs are kept until
                // logout now (.ArtCache.cs), so nothing made early is thrown away on the next hide.
                if (!DhComposeAllowed()) return null;
                var rt = DcComposeRt(bgObj, bgUv, cutObj, cutUv, "DungeonHub", out var t, out var b);
                _dhRaidComp[key] = new DhComp { Rt = rt, Top = t, Bot = b, Failed = rt == null };
                if (rt != null) { top = t; bot = b; return rt; }
            }
        }
        if (bgObj != null) { uv = bgUv; return bgObj; }
        return null;
    }

    private static void DhReleaseRt(UE.RenderTexture rt) { try { rt.Release(); UE.Object.Destroy(rt); } catch { } }
}
