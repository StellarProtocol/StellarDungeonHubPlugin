using System.Collections.Generic;
using System.Diagnostics;

namespace Stellar.DungeonHub;

// Dungeons hub — [DhPerf] timing logs for the first-open freeze (user report 2026-11-08: the FIRST open visibly froze
// the game, later opens were fine). Kept on purpose: they are how the fix is confirmed in game.
//   • DhPerfLog(stage, t0): one line per stage per SESSION (a stage that already logged stays quiet), so nothing
//     spams per frame or per poll.
//   • The frame probe: plugin-side stopwatches can't see the framework's own work (the window's first mount / first
//     Apply, which builds the uGUI tree, runs in its Band 3 after the plugin Updates). So the probe times the wall gap
//     between consecutive Framework.Update calls for a few frames after an open (or the hidden warm mount) and logs
//     the longest one — that frame's gap is the freeze the user sees, whoever spent it.
public sealed partial class Plugin
{
    private readonly HashSet<string> _dhPerfSeen = new();

    private static long DhPerfNow() => Stopwatch.GetTimestamp();
    private static double DhPerfMs(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private void DhPerfLog(string stage, long t0, string extra = "")
    {
        if (!_dhPerfSeen.Add(stage)) return;
        _services.Log.Info($"[DhPerf] {stage}: {DhPerfMs(t0):0.0} ms{(extra.Length > 0 ? " — " + extra : "")}");
    }

    // ── frame probe ───────────────────────────────────────────────────────────────────────────────────────────────
    private string _dhProbeTag = "";
    private bool   _dhProbeSub;
    private int    _dhProbeLeft, _dhProbeFrames, _dhProbeMaxAt;
    private long   _dhProbeStart, _dhProbeLast;
    private double _dhProbeMax;

    // Start timing the next `frames` frames under `tag` (once per tag per session; a probe already running is closed
    // and logged first). Call it right BEFORE the work that triggers the framework's build (SetVisible).
    private void DhProbeStart(string tag, int frames)
    {
        if (_dhPerfSeen.Contains(tag)) return;
        if (_dhProbeSub) DhProbeEnd();
        _dhProbeTag = tag; _dhProbeLeft = frames; _dhProbeFrames = 0; _dhProbeMax = 0; _dhProbeMaxAt = 0;
        _dhProbeStart = _dhProbeLast = DhPerfNow();
        _services.Framework.Update += OnDhProbeFrame;
        _dhProbeSub = true;
    }

    private void OnDhProbeFrame(float _)
    {
        long now = DhPerfNow();
        double gap = (now - _dhProbeLast) * 1000.0 / Stopwatch.Frequency;
        _dhProbeLast = now;
        _dhProbeFrames++;
        if (gap > _dhProbeMax) { _dhProbeMax = gap; _dhProbeMaxAt = _dhProbeFrames; }
        if (--_dhProbeLeft <= 0) DhProbeEnd();
    }

    private void DhProbeEnd()
    {
        if (!_dhProbeSub) return;
        try { _services.Framework.Update -= OnDhProbeFrame; } catch { }
        _dhProbeSub = false;
        if (!_dhPerfSeen.Add(_dhProbeTag)) return;
        double total = (_dhProbeLast - _dhProbeStart) * 1000.0 / Stopwatch.Frequency;
        _services.Log.Info($"[DhPerf] {_dhProbeTag}: longest frame {_dhProbeMax:0.0} ms (frame {_dhProbeMaxAt} of {_dhProbeFrames}, " +
            $"{total:0} ms in all){DhArtPerfText()}");
    }
}
