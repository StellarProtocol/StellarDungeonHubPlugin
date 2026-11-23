using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — leader "Kidnap" (ported from MahiruUtility's Dungeon Confirm, which fires the member accept 20×).
// When the party LEADER sends team_enter's AsyncReplyJoinActivity(true, …) repeatedly, the server counts those accepts
// for members who haven't pressed Ready yet and pulls them in (user report). We send exactly one per member still
// waiting (Ready == 0) instead of a fixed 20, recounted on every press — no per-confirm latch, the next request starts
// clean on its own. Split from .Window.cs / .Lua.cs (500-line cap); the footer's left slot calls DcKidnapSlot().
public sealed partial class Plugin
{
    private const double DcKidnapMsgSec = 3.0;   // "Kidnap sent · N" footer note

    // Footer note: the text is built ONCE per press (not per frame), shown only for the request it was sent on.
    private string _dcKidnapMsg = "";
    private double _dcKidnapUntil;
    private int    _dcKidnapSeq = -1;

    // Left slot, team-mode leader while the confirm is live: Primary pill, or the green off pill once nobody is waiting
    // / the entry is already "doing". Same 186×44 geometry as every footer pill, so nothing shifts.
    private HudElement DcKidnapSlot() => new ConditionalElement(() => _dc == null || _dc.Waiting == 0 || _dc.Doing,
        DcPillOff(() => L("dc.btn.kidnap"), () => DcConfirmedFill),
        DcPrimary(() => L("dc.btn.kidnap"), DcOnKidnap));

    // The footer message, with the kidnap note taking over for a few seconds after a press.
    private string DcFooterMessage(Func<string> message) =>
        _dcKidnapSeq == _dcSeq && DcNow < _dcKidnapUntil && _dcEnd == DcEnd.None ? _dcKidnapMsg : message();

    private void DcOnKidnap()
    {
        if (_dcPreview) { DcClose(); return; }
        if (_dc == null || _dcEnd != DcEnd.None || _dc.Match || !_dc.IAmLeader || _dc.Doing) return;
        int n = _dc.Waiting;
        if (n <= 0) return;
        DcLuaKidnap(_dcSeq, n);
        _dcKidnapMsg = LF("dc.s.kidnapSent", n);
        _dcKidnapUntil = DcNow + DcKidnapMsgSec;
        _dcKidnapSeq = _dcSeq;
        _services.Log.Info($"[DungeonConfirm] kidnap sent {n}");
    }

    // n × AsyncReplyJoinActivity(true, fresh token), 50 ms apart, in ONE coroutine (they're RPCs). Fresh token per call,
    // like Mahiru. Unlike DcTeamPrologue there is NO CloseEnterView anywhere — kidnapping must leave the view (and the
    // leader's Cancel Entry) up. Stops early if this request's stash was replaced or closed while the loop waits.
    private void DcLuaKidnap(int seq, int n) => DcLuaAction(seq,
        " local vm = Z.VMMgr.GetVM('team_enter')" +
        " if not Z.VMMgr.GetVM('team').CheckIsInTeam() then return end" +
        " local td = Z.DataMgr.Get('team_data')" +
        " for i = 1, " + n + " do" +
        "  if m.__stellarDC ~= st or not st.open then break end" +
        "  vm.AsyncReplyJoinActivity(true, td.CancelSource:CreateToken())" +
        "  if i < " + n + " then coroutine.wait(0.05) end" +
        " end");
}
