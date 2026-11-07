using System;
using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeon Confirm — open dead centre on EVERY open (real request and Preview), whatever position was saved, yet stay
// draggable while up (user, 2026-11-06). One SetRect right after SetVisible(true) is not enough, for three reasons
// (framework source, WindowService.cs / WindowService.Layout.cs):
//   1. Entry.SetRect is a NO-OP while the window has no Token (its first open: the mount happens later, in Band 3).
//   2. The mount then runs ApplySavedRect, which restores the per-resolution SAVED rect (the last drag) over our centre;
//      on a canvas re-create it may also park the apply and re-run it a few ticks later (ApplyPending / CanvasScaleReady).
//   3. The laid-out height settles a frame after the mount (content fit), so the first centre can use a stale size.
// So: re-centre every frame for DcCentreFrames frames after a show (a drag inside that ~0.5 s burst is undone — accepted;
// the plugin has no pointer read to end the burst early). After the burst the window is the user's: the slow DcTick
// re-centres ONLY when the canvas size changed since the last centre (resolution / UI-scale change), never because the
// window moved — an "off-centre → re-centre" check there snapped every drag back. SetRect is called only when off-centre.
public sealed partial class Plugin
{
    private const int DcCentreFrames = 30;   // ≈ 0.5 s at 60 fps — covers mount + deferred apply + content-fit settle

    private int   _dcCentreLeft;
    private bool  _dcCentreSub;
    private float _dcCentreCw, _dcCentreCh;  // canvas size the window was last centred for (0 = not yet)

    private void DcStartCentring()
    {
        DcCentreNow();   // already mounted (parked) → lands centred this frame; first open → no-op until mounted
        _dcCentreLeft = DcCentreFrames;
        if (!_dcCentreSub) { _services.Framework.Update += OnDcCentreFrame; _dcCentreSub = true; }
    }

    private void DcStopCentring()
    {
        _dcCentreLeft = 0;
        if (_dcCentreSub) { try { _services.Framework.Update -= OnDcCentreFrame; } catch { } _dcCentreSub = false; }
    }

    private void OnDcCentreFrame(float _)
    {
        if (--_dcCentreLeft < 0 || !(_dcShow5 || _dcShowRaid)) { DcStopCentring(); return; }
        DcCentreNow();
    }

    // DcTick (200 ms): follow a resolution / UI-scale change only. The burst owns the first 0.5 s.
    private void DcRecentreOnCanvasChange()
    {
        if (_dcCentreSub || !(_dcShow5 || _dcShowRaid) || _dcCentreCw <= 1f) return;
        float cw = _services.Framework.CanvasWidth, ch = _services.Framework.CanvasHeight;
        if (Math.Abs(cw - _dcCentreCw) >= 0.5f || Math.Abs(ch - _dcCentreCh) >= 0.5f) DcCentreNow();
    }

    // Centre the shown layout on the canvas (canvas units — WindowBuilder-Patterns.md §"Centering a plugin window").
    // The laid-out height once known (the content fit is the truth), else the computed design height; the position is
    // floored to whole units so an odd measured height never puts the window on a half pixel.
    private void DcCentreNow()
    {
        if (!(_dcShow5 || _dcShowRaid)) return;
        bool raid = _dcShowRaid;
        var win = raid ? _dcWinRaid : _dcWin5;
        float cw = _services.Framework.CanvasWidth, ch = _services.Framework.CanvasHeight;
        if (cw <= 1f || ch <= 1f) return;
        _dcCentreCw = cw; _dcCentreCh = ch;
        var r = win.Rect;
        float w = raid ? DcRW : Dc5W;
        float h = r.Height > 1f ? r.Height : raid ? DcRH : Dc5H;
        float x = MathF.Max(0f, MathF.Floor((cw - w) / 2f)), y = MathF.Max(0f, MathF.Floor((ch - h) / 2f));
        if (Math.Abs(r.X - x) < 0.5f && Math.Abs(r.Y - y) < 0.5f && Math.Abs(r.Width - w) < 0.5f) return;
        win.SetRect(new WindowRect(x, y, w, h));
    }
}
