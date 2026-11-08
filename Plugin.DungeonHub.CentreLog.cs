using Stellar.Abstractions.Domain;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeons hub — [DhCentre] trace of the open-centring burst (user report 2026-11-08: on a REOPEN the first drag right
// after the open is lost — the window stays dead centre — while the first open after login and every second drag work).
// For DhClWindowMs after each open it logs, one line per CHANGE (never per tick):
//   • every SetRect the centring does (from → to, and which path: burst / canvas change),
//   • the live window pose whenever it moved since the last line (with IsShown),
//   • the left mouse button going down / up (screen px) — the plugin polls the held state each tick, so a press shows
//     even when the framework never started a drag for it,
//   • when and why the burst stopped (user moved / ticks done / hidden).
// Reading it: "mouse down" → no "moved" line before "mouse up" = the framework never started the drag (press not taken);
// "moved" to the drag target then a "moved" back to centre with no "SetRect" of ours = something else re-placed it.
public sealed partial class Plugin
{
    private const double DhClWindowMs = 3000;

    private bool  _dhClSub, _dhClMouse;
    private long  _dhClT0;
    private int   _dhClOpen;
    private (float X, float Y, bool Shown) _dhClLast;

    private double DhClMs => DhPerfMs(_dhClT0);
    private bool   DhClOn => _dhClSub;

    private void DhClStart()
    {
        _dhClT0 = DhPerfNow();
        _dhClOpen++;
        _dhClMouse = DhClMouseHeld();
        var r = _dhWin.Rect;
        _dhClLast = (r.X, r.Y, _dhWin.IsShown);
        _services.Log.Info($"[DhCentre] open#{_dhClOpen}: live ({r.X:0},{r.Y:0}) {r.Width:0}x{r.Height:0} shown={_dhWin.IsShown} " +
            $"canvas {_services.Framework.CanvasWidth}x{_services.Framework.CanvasHeight} placed={DhClPos(_dhPlaced)} mouse={(_dhClMouse ? "down" : "up")} cursor {DhClCursor()}");
        if (!_dhClSub) { _services.Framework.Update += OnDhClFrame; _dhClSub = true; }
    }

    private void DhClEnd()
    {
        if (!_dhClSub) return;
        try { _services.Framework.Update -= OnDhClFrame; } catch { }
        _dhClSub = false;
    }

    // One SetRect done by the centring (call AFTER the set: `to` is the read-back, on-screen-clamped pose).
    private void DhClSet(string path, WindowRect from)
    {
        if (!DhClOn && path == "burst") return;
        var to = _dhWin.Rect;
        // A set that didn't move the window (only the width differed) is noise — it would repeat every burst tick.
        if (System.Math.Abs(to.X - from.X) < 0.5f && System.Math.Abs(to.Y - from.Y) < 0.5f) return;
        _services.Log.Info($"[DhCentre] +{(DhClOn ? DhClMs : -1):0}ms SetRect[{path}] ({from.X:0},{from.Y:0}) -> ({to.X:0},{to.Y:0}) h={to.Height:0}");
        _dhClLast = (to.X, to.Y, _dhWin.IsShown);
    }

    private void DhClStop(string why)
    {
        if (!DhClOn) return;
        var r = _dhWin.Rect;
        _services.Log.Info($"[DhCentre] +{DhClMs:0}ms burst stopped: {why} — live ({r.X:0},{r.Y:0}) placed={DhClPos(_dhPlaced)}");
    }

    // Every SetRect of ours re-bases _dhClLast (DhClSet), so a "moved" line here is a move nobody logged a SetRect for
    // = the user's drag (or a framework re-place). Subscription order vs the burst tick doesn't matter: a move shows on
    // this tick or the next one either way.
    private void OnDhClFrame(float _)
    {
        if (DhClMs > DhClWindowMs) { _services.Log.Info($"[DhCentre] +{DhClMs:0}ms trace end — live ({_dhWin.Rect.X:0},{_dhWin.Rect.Y:0})"); DhClEnd(); return; }
        var r = _dhWin.Rect;
        bool shown = _dhWin.IsShown;
        if (System.Math.Abs(r.X - _dhClLast.X) >= 0.5f || System.Math.Abs(r.Y - _dhClLast.Y) >= 0.5f || shown != _dhClLast.Shown)
        {
            _services.Log.Info($"[DhCentre] +{DhClMs:0}ms moved ({_dhClLast.X:0},{_dhClLast.Y:0}) -> ({r.X:0},{r.Y:0}) shown={shown} " +
                $"burst={(_dhCentreSub ? $"on, {_dhCentreLeft} left" : "off")}");
            _dhClLast = (r.X, r.Y, shown);
        }
        bool held = DhClMouseHeld();
        if (held != _dhClMouse)
        {
            _dhClMouse = held;
            var mp = DhClMousePos();
            _services.Log.Info($"[DhCentre] +{DhClMs:0}ms mouse {(held ? "down" : "up")} at screen ({mp.X:0},{mp.Y:0}) cursor {DhClCursor()} — window ({r.X:0},{r.Y:0}) shown={shown}");
        }
    }

    private static string DhClPos((float X, float Y)? p) => p is { } v ? $"({v.X:0},{v.Y:0})" : "none";

    private static bool DhClMouseHeld()
    {
        try { return UE.Input.GetMouseButton(0); } catch { return false; }
    }

    // Unity screen px, origin bottom-left (as the framework ticker reads it).
    private static (float X, float Y) DhClMousePos()
    {
        try { var p = UE.Input.mousePosition; return (p.x, p.y); } catch { return (-1f, -1f); }
    }

    // In the world the cursor is the camera's unless something frees it (Alt, a game panel, the sticky cursor). A press
    // with the cursor locked reads the screen centre, not where the user aimed — so a lost press shows here.
    private static string DhClCursor()
    {
        try { return $"{UE.Cursor.lockState}/{(UE.Cursor.visible ? "visible" : "hidden")}"; } catch { return "?"; }
    }
}
