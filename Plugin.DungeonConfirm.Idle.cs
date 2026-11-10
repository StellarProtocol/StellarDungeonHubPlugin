namespace Stellar.DungeonHub;

// Dungeon Confirm — idle rate inside a dungeon / raid run (raid perf, 2026-11-10). With nothing of ours on screen there
// is no reason to poll the confirm stash 5× a second through a boss fight, so the poll drops to 1 Hz.
//
// Deliberately a SLOWER poll, not a pause: the Lua wrapper keeps swallowing OpenView('team_enter') while the feature is
// ON, and a request CAN arrive inside a run (a match found while still in the instance, the game's re-open after a
// scene change — team_vm.lua:1128). With no poll at all that request would never show anywhere. At 1 Hz the window
// appears ≤ 1 s late on a 30 s countdown; the countdown origin is our first sighting either way.
//
// Full rate as soon as anything needs it: leaving the run (CurrentRunId → 0), the hub open or a page open being
// followed, a preview, a request on screen, or the last poll reporting an open request we haven't handled yet.
public sealed partial class Plugin
{
    private const int DcIdleEvery = 5;   // 1 in 5 ticks of the 200 ms tick = 1 Hz

    private bool _dcHeadOpen;            // the last poll's head said a request is open (set in DcTick)
    private int  _dcIdleTicks;

    // True = skip this tick's poll.
    private bool DcIdleSkip()
    {
        bool idle = _services.Dungeon.CurrentRunId != 0 && _dc == null && !_dcPreview &&
                    (!_dcHeadOpen || _dcDismissed) && !_dhShow && !_dhOpening;
        if (!idle) { _dcIdleTicks = 0; return false; }
        return _dcIdleTicks++ % DcIdleEvery != 0;   // the first idle tick still polls
    }
}
