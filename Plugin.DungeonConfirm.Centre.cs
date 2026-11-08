using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — open dead centre on EVERY open (real request and Preview), whatever position was saved, yet stay
// draggable while up (user, 2026-11-06). One SetRect right after SetVisible(true) is not enough, for three reasons
// (framework source, WindowService.cs / WindowService.Layout.cs):
//   1. Entry.SetRect is a NO-OP while the window has no Token (its first open: the mount happens later, in Band 3).
//   2. The mount then runs ApplySavedRect, which restores the per-resolution SAVED rect (the last drag) over our centre;
//      on a canvas re-create it may also park the apply and re-run it a few ticks later (ApplyPending / CanvasScaleReady).
//   3. The laid-out height settles a frame after the mount (content fit), so the first centre can use a stale size.
// So: re-centre every plugin tick for DcCentreFrames ticks after a show. After the burst the window is the user's: the
// slow DcTick re-centres ONLY when the canvas size changed since the last centre (resolution / UI-scale change), never
// because the window moved — an "off-centre → re-centre" check there snapped every drag back. SetRect is called only
// when off-centre.
// ⚠️ The burst must END the moment the user drags (user report 2026-11-08: "the first drag after opening snaps back").
// Framework.Update is the per-plugin rate-gated tick (default 30 Hz, and one tick per frame when frames are slow), so
// 30 ticks is ≥ 1 s — several seconds across a hitchy first open — and every tick of it put the window back at centre
// under a drag in progress. So each burst tick first compares the live position with where we last left a MOUNTED
// window (DcPlacedPos): any difference we didn't cause is the user's drag → stop centring for good. The mount's own
// restore (reason 2) can't trip it — nothing is recorded until the window is mounted (IsShown), and size changes
// (content fit / min-width widen, top-left anchored) never move X/Y.
public sealed partial class Plugin
{
    private const int DcCentreFrames = 30;   // plugin ticks — covers mount + deferred apply + content-fit settle

    private int   _dcCentreLeft;
    private bool  _dcCentreSub;
    private float _dcCentreCw, _dcCentreCh;  // canvas size the window was last centred for (0 = not yet)
    private (float X, float Y)? _dcPlaced;   // where we last left the mounted window (null = not mounted / not placed yet)

    // The position we just left `win` at, or null while it isn't mounted (Rect is then the DefaultRect, not the live pose).
    private static (float X, float Y)? DcPlacedPos(IWindowControl win)
    {
        if (!win.IsShown) return null;
        var r = win.Rect;
        return (r.X, r.Y);
    }

    // True when the mounted window is no longer where we left it = the user dragged it (see the ⚠️ above).
    private static bool DcUserMoved(IWindowControl win, (float X, float Y)? placed)
    {
        if (placed is not { } p || !win.IsShown) return false;
        var r = win.Rect;
        return Math.Abs(r.X - p.X) >= 0.5f || Math.Abs(r.Y - p.Y) >= 0.5f;
    }

    private void DcStartCentring()
    {
        _dcPlaced = null;
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
        if (DcUserMoved(_dcShowRaid ? _dcWinRaid : _dcWin5, _dcPlaced)) { DcStopCentring(); return; }   // the user's now
        DcCentreNow();
    }

    // DcTick (200 ms): follow a resolution / UI-scale change only. The burst owns the first ticks after a show.
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
        if (!(Math.Abs(r.X - x) < 0.5f && Math.Abs(r.Y - y) < 0.5f && Math.Abs(r.Width - w) < 0.5f))
            win.SetRect(new WindowRect(x, y, w, h));
        _dcPlaced = DcPlacedPos(win);   // read back: SetRect clamps on-screen, so record the live pose, not (x, y)
    }
}
