using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeons hub — composing the NPC tab's art (2026-11-21, user-approved): the banner = scenery backdrop + emblem, the
// thumb = the same backdrop cropped + dimmed (else a MultiTool-style gradient tile, Stellar-Design-Language.md §7) +
// map marker. Called from .NpcArt.cs.
//
// GPU only, and only Graphics.Blit — its orientation is guaranteed (the raid compose, .ArtCompose.cs, falls back to
// DrawTexture only for a mismatched shape). A Blit covers the WHOLE target, so an overlay that sits in a sub-rect is
// placed with Blit's scale/offset (target uv → source uv) from a CLAMPED source whose 1 px border is transparent:
// everything outside the overlay's rect samples that border = transparent. Steps for one overlay (DhNpcOverlay):
//   1. its uv rect (an atlas sprite's, or the whole texture) → a temporary RT of its own pixel size (scale/offset copy);
//   2. ReadPixels that RT into a Texture2D 2 px larger, at (1, 1), over zeroed bytes (LoadRawTextureData of a zero
//      byte[] = one memcpy — never a managed Color32[] through the interop indexer, ChatTools 2026-10-24), Clamp, Apply;
//   3. Blit it with scale/offset into a temporary RT the size of the target (straight alpha);
//   4. alpha-blend that over the target with the raid compose's Sprites/Default material (premultiplied blend keeps the
//      target's alpha at 1, see .ArtCompose.cs step 3).
// The darkening is a full-rect blend of a 1×1 black texture at DhNpcBgDim alpha.
// One ReadPixels (a CPU↔GPU sync) per overlay, once per hub open — like the raid compose's opaque-band readback.
// Lifetime: composed RTs live in _dhComp (released on every hide, .Art.cs DhReleaseArt, recomposed on the next open);
// the gradient tiles and the dim texture are generated once per session and destroyed in DhDisposeNpcArt.
public sealed partial class Plugin
{
    private const float DhNpcBgFocus = 0.50f;     // backdrop crop: the kept strip's centre, fraction from the top (tune)
    private const float DhNpcBgDim = 0.22f;       // backdrop darkened this far toward black, so the emblem reads
    private const float DhNpcEmblemH = 0.84f;     // emblem height / band height (drawn whole, centred)
    private const float DhNpcEmblemMaxUp = 2f;    // an emblem never drawn past this × its native pixels (tune)
    private const float DhNpcIconH = 0.72f;       // map marker height / thumb height (aspect-fit, centred)
    private const int   DhNpcBandMaxW = 1304;     // band RT width cap (2× the 652-unit band)
    private const int   DhNpcOverlayMax = 1024;   // an overlay's copy is capped at this size

    // Diagonal gradient pairs, top-left → bottom-right (user-picked; no yellow / orange). One per NPC: npcId % count.
    private static readonly (uint A, uint B)[] DhGradPal =
    {
        (0x2f8fb8, 0x5fd38d),   // teal → green
        (0x8a4fd8, 0xc8508f),   // purple → pink
        (0x2b3f9e, 0x7448c8),   // indigo → violet
        (0x1f6f8b, 0x2b3f9e),   // teal → indigo
        (0xa8324a, 0x6a2f8a),   // crimson → plum
        (0x2f7a5a, 0x1f4f7a),   // emerald → navy
    };

    private readonly UE.Texture2D?[] _dhGrad = new UE.Texture2D?[DhGradPal.Length];
    private readonly bool[] _dhGradFailed = new bool[DhGradPal.Length];
    private UE.Texture2D? _dhDim;

    private static int DhGradIndex(int npcId) => (int)((uint)npcId % (uint)DhGradPal.Length);

    // ── composed RT cache (shares _dhComp with the raid pairs, so DhReleaseArt frees it on hide) ─────────────────────
    private UE.RenderTexture? DhNpcComposed(string key, out bool failed)
    {
        failed = false;
        if (!_dhComp.TryGetValue(key, out var c)) return null;
        if (c.Failed) { failed = true; return null; }
        bool alive = false;
        try { alive = c.Rt != null && c.Rt.IsCreated(); } catch { }
        if (alive) return c.Rt;
        if (c.Rt != null) DhReleaseRt(c.Rt);   // lost (device reset) → compose again
        _dhComp.Remove(key);
        return null;
    }

    private void DhNpcKeep(string key, UE.RenderTexture? rt) => _dhComp[key] = new DhComp { Rt = rt, Failed = rt == null };

    // ── the gradient tile: generated once per palette entry at 2× the thumb (204×136), bottom row first ──────────────
    // Rounding comes from the thumb's GameTextureElement CornerRadius (.Art.cs), so the gradient stays inside the
    // tile's rounded rect; the texture itself is a plain rectangle.
    private UE.Texture2D? DhGradTex(int i)
    {
        if (_dhGrad[i] != null || _dhGradFailed[i]) return _dhGrad[i];
        int w = Math.Max(2, (int)MathF.Round(DhThumbW * 2f)), h = Math.Max(2, (int)MathF.Round(DhThumbH * 2f));
        UE.Texture2D? t = null;
        try
        {
            var (ca, cb) = DhGradPal[i];
            float ar = (ca >> 16 & 0xff), ag = (ca >> 8 & 0xff), ab = (ca & 0xff);
            float br = (cb >> 16 & 0xff), bg = (cb >> 8 & 0xff), bb = (cb & 0xff);
            float hiR = 0.85f * w;   // the top-left highlight's radius, in px
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                float fromTop = 1f - (float)y / (h - 1);
                for (int x = 0; x < w; x++)
                {
                    float k = ((float)x / (w - 1) + fromTop) * 0.5f;   // 0 top-left → 1 bottom-right
                    float r = ar + (br - ar) * k, g = ag + (bg - ag) * k, b = ab + (bb - ab) * k;
                    // soft white highlight at the top-left corner, ~25 % at the corner, quadratic falloff
                    float dx = x, dy = (h - 1) - y, d = MathF.Sqrt(dx * dx + dy * dy) / hiR;
                    if (d < 1f) { float s = 0.25f * (1f - d) * (1f - d); r += (255f - r) * s; g += (255f - g) * s; b += (255f - b) * s; }
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(r + 0.5f); px[o + 1] = (byte)(g + 0.5f); px[o + 2] = (byte)(b + 0.5f); px[o + 3] = 255;
                }
            }
            t = new UE.Texture2D(w, h, UE.TextureFormat.RGBA32, false)
                { name = "StellarDhNpcGrad", hideFlags = UE.HideFlags.HideAndDontSave, wrapMode = UE.TextureWrapMode.Clamp };
            t.LoadRawTextureData(px);   // byte[] → Il2CppStructArray<byte> = one memcpy
            t.Apply(false, true);       // non-readable after upload: nothing reads it back
            _dhGrad[i] = t;
        }
        catch (Exception ex)
        {
            _dhGradFailed[i] = true;
            _services.Log.Warning($"[DungeonHub] npc gradient {i} failed: {ex.Message} — marker on the plain tile");
            if (t != null) { try { UE.Object.Destroy(t); } catch { } }
        }
        return _dhGrad[i];
    }

    // ── banner: backdrop crop (already the band's aspect) → dim → emblem whole, centred ─────────────────────────────
    // dw / dh out = the emblem's drawn size in RT px (for the log).
    private UE.RenderTexture? DhNpcComposeBand(object bgObj, UvRect bgCrop, object embObj, UvRect embUv, float box, out float dw, out float dh)
    {
        var prev = UE.RenderTexture.active;
        UE.RenderTexture? rt = null;
        dw = dh = 0f;
        try
        {
            var bg = DcAsTex(bgObj); var emb = DcAsTex(embObj);
            if (bg == null || emb == null || !DhNpcMaterials()) return null;
            float cw = bg.width * bgCrop.W;
            int w = Math.Clamp((int)MathF.Round(cw), 2, DhNpcBandMaxW), h = Math.Max(2, (int)MathF.Round(w / box));
            rt = DhNpcNewRt(w, h);
            UE.Graphics.Blit(bg, rt, new UE.Vector2(bgCrop.W, bgCrop.H), new UE.Vector2(bgCrop.X, bgCrop.Y));
            UE.Graphics.Blit(_dhDim!, rt, _dcCompMat);
            float ew = emb.width * embUv.W, eh = emb.height * embUv.H;
            dh = h * DhNpcEmblemH; dw = dh * ew / Math.Max(1f, eh);
            if (dw > w * 0.92f) { dh *= w * 0.92f / dw; dw = w * 0.92f; }   // a wide emblem: fit the width instead
            // A small atlas icon (com_icon_tab_*, life_profession_*) would be blown up to the band's height and blur:
            // capped at DhNpcEmblemMaxUp × its own pixels ON SCREEN — w RT px span the DhDetInW-unit band.
            float cap = DhNpcEmblemMaxUp * Math.Max(1f, eh) * w / DhDetInW;
            if (dh > cap) { dw *= cap / dh; dh = cap; }
            if (!DhNpcOverlay(rt, emb, embUv, (w - dw) / 2f, (h - dh) / 2f, dw, dh)) { DhReleaseRt(rt); return null; }
            return rt;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[DungeonHub] npc banner compose failed: {ex.Message} — emblem alone");
            if (rt != null) DhReleaseRt(rt);
            return null;
        }
        finally { UE.RenderTexture.active = prev; }
    }

    // ── thumb: base (backdrop crop + dim, or the gradient tile) → map marker aspect-fit, centred ──────────────────────
    // Made at 2× the thumb (204×136, the gradient's own size). No marker (null) = the base alone (a darkened backdrop
    // still needs this RT). No top-left highlight on a photo: it only lives in the gradient texture.
    private UE.RenderTexture? DhNpcComposeThumb(object baseObj, UvRect baseUv, bool dim, object? iconObj, UvRect iconUv)
    {
        var prev = UE.RenderTexture.active;
        UE.RenderTexture? rt = null;
        try
        {
            var bas = DcAsTex(baseObj);
            var icon = iconObj != null ? DcAsTex(iconObj) : null;
            if (bas == null || (iconObj != null && icon == null) || !DhNpcMaterials()) return null;
            int w = Math.Max(2, (int)MathF.Round(DhThumbW * 2f)), h = Math.Max(2, (int)MathF.Round(DhThumbH * 2f));
            rt = DhNpcNewRt(w, h);
            UE.Graphics.Blit(bas, rt, new UE.Vector2(baseUv.W, baseUv.H), new UE.Vector2(baseUv.X, baseUv.Y));
            if (dim) UE.Graphics.Blit(_dhDim!, rt, _dcCompMat);
            if (icon == null) return rt;
            float iw = icon.width * iconUv.W, ih = icon.height * iconUv.H;
            float dh = h * DhNpcIconH, dw = dh * iw / Math.Max(1f, ih);
            if (dw > w * DhNpcIconH) { dh *= w * DhNpcIconH / dw; dw = w * DhNpcIconH; }
            if (!DhNpcOverlay(rt, icon, iconUv, (w - dw) / 2f, (h - dh) / 2f, dw, dh)) { DhReleaseRt(rt); return null; }
            return rt;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[DungeonHub] npc thumb compose failed: {ex.Message} — marker alone");
            if (rt != null) DhReleaseRt(rt);
            return null;
        }
        finally { UE.RenderTexture.active = prev; }
    }

    private static UE.RenderTexture DhNpcNewRt(int w, int h)
    {
        var rt = new UE.RenderTexture(w, h, 0, UE.RenderTextureFormat.ARGB32, UE.RenderTextureReadWrite.sRGB)
            { name = "StellarDhNpcArt", hideFlags = UE.HideFlags.HideAndDontSave };
        rt.Create();
        return rt;
    }

    // The raid compose's shared blend material (.ArtCompose.cs, destroyed in DisposeDcArt) + our 1×1 dim texture.
    private bool DhNpcMaterials()
    {
        _dcCompMat ??= DcBlendMaterial();
        if (_dcCompMat == null) { _services.Log.Warning("[DungeonHub] npc art: no blend shader (Sprites/Default, UI/Default)"); return false; }
        if (_dhDim == null)
        {
            _dhDim = new UE.Texture2D(1, 1, UE.TextureFormat.RGBA32, false)
                { name = "StellarDhNpcDim", hideFlags = UE.HideFlags.HideAndDontSave, wrapMode = UE.TextureWrapMode.Clamp };
            _dhDim.SetPixel(0, 0, new UE.Color(0f, 0f, 0f, DhNpcBgDim));
            _dhDim.Apply(false, true);
        }
        return true;
    }

    // Steps 1-4 of the header note: `src`'s uv rect drawn into `dst` at (x, y, dw, dh) px, y from the TOP.
    private bool DhNpcOverlay(UE.RenderTexture dst, UE.Texture src, UvRect uv, float x, float y, float dw, float dh)
    {
        int sw = Math.Clamp((int)MathF.Round(src.width * uv.W), 2, DhNpcOverlayMax);
        int sh = Math.Clamp((int)MathF.Round(src.height * uv.H), 2, DhNpcOverlayMax);
        int bw = sw + 2, bh = sh + 2, w = dst.width, h = dst.height;
        if (dw < 1f || dh < 1f) return false;
        UE.RenderTexture? copy = null, placed = null;
        UE.Texture2D? bordered = null;
        try
        {
            copy = UE.RenderTexture.GetTemporary(sw, sh, 0, UE.RenderTextureFormat.ARGB32, UE.RenderTextureReadWrite.sRGB);
            UE.Graphics.Blit(src, copy, new UE.Vector2(uv.W, uv.H), new UE.Vector2(uv.X, uv.Y));
            bordered = new UE.Texture2D(bw, bh, UE.TextureFormat.RGBA32, false)
                { hideFlags = UE.HideFlags.HideAndDontSave, wrapMode = UE.TextureWrapMode.Clamp, filterMode = UE.FilterMode.Bilinear };
            bordered.LoadRawTextureData(new byte[bw * bh * 4]);   // all transparent
            UE.RenderTexture.active = copy;
            bordered.ReadPixels(new UE.Rect(0, 0, sw, sh), 1, 1, false);
            bordered.Apply(false, false);
            // Target uv (u, v; v from the bottom) → bordered uv: the content spans [1, 1 + sw] px of bw.
            float fx = x / w, fw = dw / w, fy = (h - y - dh) / h, fh = dh / h;
            float kx = sw / (bw * fw), ky = sh / (bh * fh);
            placed = UE.RenderTexture.GetTemporary(w, h, 0, UE.RenderTextureFormat.ARGB32, UE.RenderTextureReadWrite.sRGB);
            UE.Graphics.Blit(bordered, placed, new UE.Vector2(kx, ky), new UE.Vector2(1f / bw - fx * kx, 1f / bh - fy * ky));
            UE.Graphics.Blit(placed, dst, _dcCompMat);
            return true;
        }
        finally
        {
            if (copy != null) UE.RenderTexture.ReleaseTemporary(copy);
            if (placed != null) UE.RenderTexture.ReleaseTemporary(placed);
            if (bordered != null) { try { UE.Object.Destroy(bordered); } catch { } }
        }
    }

    private void DhDisposeNpcArt()
    {
        for (int i = 0; i < _dhGrad.Length; i++)
            if (_dhGrad[i] != null) { try { UE.Object.Destroy(_dhGrad[i]); } catch { } _dhGrad[i] = null; }
        if (_dhDim != null) { try { UE.Object.Destroy(_dhDim); } catch { } _dhDim = null; }
    }
}
