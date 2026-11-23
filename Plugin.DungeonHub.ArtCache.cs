using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeons hub — art caches that outlive a hide (2026-11-23).
//   • Crops: a list thumb's display uv is cached PER ENTRY (by Key: entries are new objects on every catalog parse, the
//     DhCrop is re-attached once per parse via DhEntry.ThumbCrop), not per list slot — the list is a recycled pool now
//     (.Window.cs), so a slot shows a different entry as it scrolls. The crop is recomputed only when one of its inputs
//     changes (art, backdrop, composed or not, focus, box aspect): compared field by field, so the per-frame texture
//     Funcs allocate nothing (the old key was a $"" string built on every call).
//   • Composed RenderTextures (raid pairs here, NPC thumbs / bands in .NpcCompose.cs) are KEPT across hide / show. They
//     used to be released on every hide, so every open recomposed them at one per frame and the thumbs popped in one by
//     one. Released now only by DhReleaseArt: logout (the next character), plugin Dispose — and a single RT the device
//     lost (!IsCreated) is dropped and recomposed where it is read.
// Memory: a raid pair is ≤ DcCompMaxW wide, an NPC thumb 204×136, an NPC band ≤ 1304 wide — a few tens of MB at most
// once every tab and every NPC banner has been seen.
public sealed partial class Plugin
{
    private sealed class DhCrop
    {
        public string Art = "", Bg = "";
        public bool Set, Composed;
        public float Top, Bot, Aspect;
        public UvRect Uv = new(0f, 0f, 1f, 1f);
    }

    private readonly Dictionary<string, DhCrop> _dhThumbCrops = new();
    private readonly DhCrop _dhBandCrop = new();
    // Raid (backdrop, cut-out) pairs: a tuple key, so a lookup builds no string (the NPC composes keep _dhComp's string
    // keys, built once per NPC).
    private readonly Dictionary<(string Bg, string Cut), DhComp> _dhRaidComp = new();

    private DhCrop DhThumbCropOf(DhEntry e)
    {
        if (e.ThumbCrop != null) return e.ThumbCrop;
        if (!_dhThumbCrops.TryGetValue(e.Key, out var c)) _dhThumbCrops[e.Key] = c = new DhCrop();
        return e.ThumbCrop = c;
    }

    private UvRect DhThumbUvOf(DhEntry? e) => e == null ? DhFullUv : DhThumbCropOf(e).Uv;

    // DcCrop only when an input changed since the last call for this box.
    private static void DhCropTo(DhCrop c, object tex, UvRect uv, string art, string bg, float aspect, float top, float bot)
    {
        bool composed = tex is UE.RenderTexture;
        if (c.Set && c.Composed == composed && c.Top == top && c.Bot == bot && c.Aspect == aspect && c.Art == art && c.Bg == bg)
            return;
        c.Set = true; c.Composed = composed; c.Top = top; c.Bot = bot; c.Aspect = aspect; c.Art = art; c.Bg = bg;
        c.Uv = DcCrop(tex, uv, aspect, top, bot);
    }

    private void DhReleaseArt()
    {
        foreach (var c in _dhComp.Values) if (c.Rt != null) DhReleaseRt(c.Rt);
        _dhComp.Clear();
        foreach (var c in _dhRaidComp.Values) if (c.Rt != null) DhReleaseRt(c.Rt);
        _dhRaidComp.Clear();
        _dhBandCrop.Set = false;
        foreach (var c in _dhThumbCrops.Values) c.Set = false;
    }
}
