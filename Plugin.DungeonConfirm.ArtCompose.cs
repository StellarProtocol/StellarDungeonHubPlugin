using System;
using System.Runtime.InteropServices;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeon Confirm — the raid art strip, composed from two game images.
//
// RaidDungeonTable.MonsterShowTexture is a TRANSPARENT boss cut-out: on its own it showed the window colour on the right
// and top-left (2026-11-06 raid entry). The raid menu draws it over DiffBgTexture (rimg_monster over rimg_bg,
// raid_main_view.lua:315-317), so this does the same, ONCE per (backdrop, cut-out) pair, into one RenderTexture that the
// strip's GameTextureElement shows like any other texture (a RenderTexture is a Texture; same uv-crop logic):
//   1. RenderTexture sized to the backdrop (its uv rect in pixels; capped at DcCompMaxW wide — the strip shows the full
//      width at 1000 px, so more is wasted VRAM). sRGB, no depth, like the minimap readback (Minimap-World-Projection.md).
//   2. Graphics.Blit the backdrop (plain copy, its uv rect via scale/offset).
//   3. Alpha-blend the cut-out over it with Sprites/Default. ⚠️ Sprites/Default, not UI/Default: it premultiplies
//      (Blend One OneMinusSrcAlpha), so the destination alpha stays 1; UI/Default's SrcAlpha/OneMinusSrcAlpha applies to
//      alpha too (a·a + (1−a)), which leaves the soft edges semi-transparent and the window shows through. UI/Default
//      is only the fallback. Placement: the prefab's anchoring isn't in the dumps, so the cut-out is fitted to the
//      backdrop's HEIGHT and centred horizontally. Same aspect (the expected case: both are full-screen layers) → a
//      full-rect Blit with the material (Blit's orientation is guaranteed); otherwise Graphics.DrawTexture into the RT
//      under GL.LoadPixelMatrix, with an explicit white vertex colour (DrawTexture's default colour is the GUI shader's
//      0.5 "neutral", which would halve Sprites/Default's output).
//   4. The cut-out's opaque rows are read back once (a 64 px wide Blit → ReadPixels, as MechMapArtSource) so the strip's
//      crop can keep the bosses' heads in view (DcCrop's focus band).
// Lifetime: one RT at a time. Released on a pair change, on DcHide and on Dispose; recomposed if the RT lost its
// contents (IsCreated() false after a device reset). A pair that fails to compose falls back to the backdrop alone.
// While either image is still loading the strip shows its empty tile; after DcCompWaitSec with only one image loaded,
// that one is shown alone.
public sealed partial class Plugin
{
    private const int    DcCompMaxW = 2048;
    private const double DcCompWaitSec = 3.0;

    private UE.RenderTexture? _dcComp;
    private UE.Material? _dcCompMat;
    private string _dcCompKey = "", _dcCompFailKey = "";
    private double _dcCompSince = -1;
    private float _dcCompTop = 0.40f, _dcCompBot = 0.40f;   // cut-out opaque band, fractions from the top

    // The composed raid art (or a single-image fallback). `uv` = the uv to crop from, `top`/`bot` = the focus band.
    private object? DcRaidArt(string bgPath, string cutPath, out UvRect uv, out float top, out float bot)
    {
        uv = new UvRect(0f, 0f, 1f, 1f); top = bot = DcArtFocus;
        string key = bgPath + "|" + cutPath;
        if (key != _dcCompKey) { DcReleaseComp(); _dcCompKey = key; _dcCompSince = DcNow; }
        try
        {
            if (_dcComp != null && _dcComp.IsCreated()) { top = _dcCompTop; bot = _dcCompBot; return _dcComp; }
        }
        catch { }
        if (_dcComp != null) DcReleaseComp(keepKey: true);   // contents lost (device reset) → compose again

        var bgObj  = _services.GameAssets.LoadByPath(bgPath, out var bgUv);
        var cutObj = _services.GameAssets.LoadByPath(cutPath, out var cutUv);
        if (bgObj != null && cutObj != null && _dcCompFailKey != key)
        {
            if (DcCompose(bgObj, bgUv, cutObj, cutUv)) { top = _dcCompTop; bot = _dcCompBot; return _dcComp; }
            _dcCompFailKey = key;   // don't retry every frame
        }
        bool waited = DcNow - _dcCompSince >= DcCompWaitSec || _dcCompFailKey == key;
        if (bgObj != null && (cutObj == null ? waited : _dcCompFailKey == key)) { uv = bgUv; return bgObj; }
        if (cutObj != null && bgObj == null && waited) { uv = cutUv; return cutObj; }
        return null;
    }

    private bool DcCompose(object bgObj, UvRect bgUv, object cutObj, UvRect cutUv)
    {
        var rt = DcComposeRt(bgObj, bgUv, cutObj, cutUv, "DungeonConfirm", out _dcCompTop, out _dcCompBot);
        if (rt == null) { DcReleaseComp(keepKey: true); return false; }
        _dcComp = rt;
        return true;
    }

    // Steps 1-4 of the header note, for any caller (Dungeon Confirm's one strip, the Dungeons hub's raid art): a NEW
    // RenderTexture the caller owns (release + destroy it), or null with a log line. `tag` names the caller in the log.
    // The blend material is shared and lives until DisposeDcArt.
    private UE.RenderTexture? DcComposeRt(object bgObj, UvRect bgUv, object cutObj, UvRect cutUv, string tag,
        out float top, out float bot)
    {
        top = bot = DcArtFocus;
        var prev = UE.RenderTexture.active;
        UE.RenderTexture? rt = null;
        try
        {
            var bg = DcAsTex(bgObj); var cut = DcAsTex(cutObj);
            if (bg == null || cut == null) return null;
            float bw = bg.width * bgUv.W, bh = bg.height * bgUv.H, cw = cut.width * cutUv.W, ch = cut.height * cutUv.H;
            if (bw < 2f || bh < 2f || cw < 2f || ch < 2f) return null;
            float k = Math.Min(1f, DcCompMaxW / bw);
            int w = Math.Max(2, (int)MathF.Round(bw * k)), h = Math.Max(2, (int)MathF.Round(bh * k));

            _dcCompMat ??= DcBlendMaterial();
            if (_dcCompMat == null) { _services.Log.Warning($"[{tag}][Art] no blend shader (Sprites/Default, UI/Default) — raid backdrop alone"); return null; }

            rt = new UE.RenderTexture(w, h, 0, UE.RenderTextureFormat.ARGB32, UE.RenderTextureReadWrite.sRGB)
                { name = "StellarDcRaidArt", hideFlags = UE.HideFlags.HideAndDontSave };
            rt.Create();
            UE.Graphics.Blit(bg, rt, new UE.Vector2(bgUv.W, bgUv.H), new UE.Vector2(bgUv.X, bgUv.Y));

            // Fit the cut-out's height, centre it horizontally.
            float dw = cw * h / ch, dx = (w - dw) / 2f;
            bool fullUv = cutUv.X == 0f && cutUv.Y == 0f && cutUv.W == 1f && cutUv.H == 1f;
            if (fullUv && Math.Abs(dw - w) <= w * 0.01f)
                UE.Graphics.Blit(cut, rt, _dcCompMat);
            else
            {
                UE.RenderTexture.active = rt;
                UE.GL.PushMatrix();
                UE.GL.LoadPixelMatrix(0f, w, h, 0f);
                UE.Graphics.DrawTexture(new UE.Rect(dx, 0f, dw, h), cut, new UE.Rect(cutUv.X, cutUv.Y, cutUv.W, cutUv.H),
                    0, 0, 0, 0, UE.Color.white, _dcCompMat);
                UE.GL.PopMatrix();
                UE.RenderTexture.active = prev;
            }
            DcOpaqueBand(cut, cutUv, out top, out bot);
            _services.Log.Info($"[{tag}][Art] composed {w}x{h} (backdrop {bw:0}x{bh:0}, cut-out {cw:0}x{ch:0} → {dw:0} wide at x {dx:0}, " +
                $"{(fullUv && Math.Abs(dw - w) <= w * 0.01f ? "blit" : "drawtexture")}, {_dcCompMat.shader.name}); " +
                $"opaque band {top:0.00}-{bot:0.00} from top");
            return rt;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[{tag}][Art] compose failed: {ex.Message} — raid backdrop alone");
            if (rt != null) { try { rt.Release(); UE.Object.Destroy(rt); } catch { } }
            return null;
        }
        finally { UE.RenderTexture.active = prev; }
    }

    private static UE.Texture? DcAsTex(object o) => o switch
    {
        UE.Texture t => t,
        UE.Sprite s  => s.texture,   // LoadByPath's uv is already the sprite's rect in this texture
        _ => null,
    };

    private static UE.Material? DcBlendMaterial()
    {
        foreach (var n in new[] { "Sprites/Default", "UI/Default" })
        {
            var sh = UE.Shader.Find(n);
            if (sh != null) return new UE.Material(sh) { hideFlags = UE.HideFlags.HideAndDontSave };
        }
        return null;
    }

    // Rows of the cut-out with any pixel at alpha > 1/8, as fractions from the top. Falls back to DcArtFocus (a point
    // band = the old fixed focus) if the readback fails or the image is empty.
    private void DcOpaqueBand(UE.Texture cut, UvRect uv, out float top, out float bot)
    {
        top = bot = DcArtFocus;
        int w = 64, h = Math.Clamp((int)MathF.Round(64f * cut.height * uv.H / Math.Max(1f, cut.width * uv.W)), 8, 256);
        UE.RenderTexture? rt = null; UE.Texture2D? rb = null;
        var prev = UE.RenderTexture.active;
        try
        {
            rt = UE.RenderTexture.GetTemporary(w, h, 0, UE.RenderTextureFormat.ARGB32, UE.RenderTextureReadWrite.sRGB);
            UE.Graphics.Blit(cut, rt, new UE.Vector2(uv.W, uv.H), new UE.Vector2(uv.X, uv.Y));
            UE.RenderTexture.active = rt;
            rb = new UE.Texture2D(w, h, UE.TextureFormat.RGBA32, false);
            rb.ReadPixels(new UE.Rect(0, 0, w, h), 0, 0, false);   // CPU copy only — no Apply()
            var bytes = MemoryMarshal.AsBytes(rb.GetPixels32().AsSpan());   // RGBA, row 0 = BOTTOM
            if (bytes.Length != w * h * 4) return;
            int lo = -1, hi = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bytes[(y * w + x) * 4 + 3] > 32) { if (lo < 0) lo = y; hi = y; break; }
            if (lo < 0) return;
            top = 1f - (hi + 1f) / h; bot = 1f - (float)lo / h;
        }
        catch (Exception ex) { _services.Log.Warning($"[DungeonConfirm][Art] opaque-band readback failed: {ex.Message}"); }
        finally
        {
            UE.RenderTexture.active = prev;
            if (rt != null) UE.RenderTexture.ReleaseTemporary(rt);
            if (rb != null) UE.Object.Destroy(rb);
        }
    }

    // keepKey: the same pair will be composed again (contents lost / failed attempt), so its wait timer keeps running.
    private void DcReleaseComp(bool keepKey = false)
    {
        if (_dcComp != null)
        {
            try { _dcComp.Release(); UE.Object.Destroy(_dcComp); } catch { }
            _dcComp = null;
        }
        if (!keepKey) { _dcCompKey = ""; _dcCompFailKey = ""; }
    }

    private void DisposeDcArt()
    {
        DcReleaseComp();
        if (_dcCompMat != null) { try { UE.Object.Destroy(_dcCompMat); } catch { } _dcCompMat = null; }
    }
}
