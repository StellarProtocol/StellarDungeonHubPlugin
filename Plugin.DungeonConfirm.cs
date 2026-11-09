using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — replaces the game's full-screen dungeon-entry confirm (view team_enter: 5-player AND 20-player, team
// vote AND match found) with a house-style window. User-approved design 2026-11-06 (prototype v3). Game facts:
// Knowledge Base\Dungeon-Confirm-Replacement.md.
//
// Files: this one = config, hotkey, the 200 ms tick / state machine. .Lua = the OpenView/CloseView
// wrappers, the poll chunk and the action calls. .Model = snapshot parse. .Window / .Raid = the two layouts. .Art = art,
// icons and the generated glyph PNGs. .Preview = fake data for checking the look without a party. .Sound / .SoundFiles =
// our open sound + countdown ticks (replacing the game's MatchAudio cue) and the user's own sound files. .Settings = the
// settings window.
//
// Flow: ON → the Lua wrapper stashes every OpenView('team_enter') instead of opening it. The tick polls the stash; a new
// seq = a new request → once a valid snapshot parses, show our window (front + centred, .Centre). No valid snapshot within
// DcFallbackSec → log "[DungeonConfirm] fallback" and open the game's own view with the stashed args (the only case
// where the native view appears while ON). The game closing the view (CloseView) or a decline / local timeout → an end
// state held DcHoldSec, then hide; everyone ready + game closed → hide at once.
public sealed partial class Plugin
{
    private const float DcFallbackSec = 1.5f, DcHoldSec = 3f, DcArmSec = 3f;

    private IWindowControl _dcSettingsWindow = null!;
    private IDisposable? _dcHotkey, _dcTick;
    private bool _dcReplace;

    // ── state of the request on screen ────────────────────────────────────────────────────────────────────────────
    private enum DcEnd { None, Declined, TimedOut, Cancelled }

    private DcSnap? _dc;                    // what the window draws (real or preview)
    private bool    _dcPreview;             // fake data; buttons only close; no Lua at all
    private string  _dcStashId = "";        // Lua stash identity (table address) — a relog makes a new one
    private int     _dcSeq = -1;            // request seq within that stash
    private double  _dcSeenAt;              // TimeNow when this seq was first seen (countdown origin)
    private bool    _dcShownOnce, _dcDismissed, _dcConfirmed, _dcMatchDeclined, _dcScenePending;
    private DcEnd   _dcEnd;
    private string  _dcEndName = "";        // end-state name: who declined / the one who didn't answer ("You" = me)
    private int     _dcEndCount;            // TimedOut: how many didn't answer (header says "N didn't answer" when > 1)
    private double  _dcEndUntil, _dcArmUntil;

    private double DcNow => _services.Framework.TimeNow;
    private double DcRemaining => _dc == null ? 0 : Math.Max(0, _dc.TotalSec - (DcNow - _dcSeenAt));

    private void InitDungeonConfirm()
    {
        // Opt-OUT (default ON since 2026-11-08): a user who turned it off keeps OFF — the saved value wins.
        _dcReplace = _cfg.Get<bool>("dungeon_confirm_replace", true);
        _dcSettingsWindow = DcRegisterSettings();   // house-style settings (.DungeonConfirm.Settings.cs)
        InitDcWindows();   // Plugin.DungeonConfirm.Window.cs
        InitDcSound();     // Plugin.DungeonConfirm.Sound.cs (open sound + countdown ticks)

        // Registered UNBOUND (null default key) — discoverable/rebindable in the hotkey settings, user assigns the key.
        _dcHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("dungeonhub.confirm-toggle", L("dc.hotkey.toggle"), null),
            callback: () => { if (_services.ClientState.Phase == GamePhase.World) ApplyDcReplace(!_dcReplace); });

        _services.ClientState.Logout       += OnDcLogout;
        _services.ClientState.SceneChanged += OnDcSceneChanged;
        // One cheap tick: it early-outs when OFF with no preview up. While ON it also (re)installs the wrapper after a
        // relog re-created Z.UIMgr (the poll reports 'X'), so there's no separate retry loop like DungeonResult's.
        _dcTick = _services.Framework.Every(TimeSpan.FromMilliseconds(200), DcTick);
        if (_dcReplace) DcSetFlag(true);   // no-op until Lua is ready; the tick re-applies it
    }

    private void DisposeDungeonConfirm()
    {
        _dcTick?.Dispose();
        _dcHotkey?.Dispose();
        DcStopCentring();
        DisposeDcArt();   // the composed raid art RT + its blend material (.ArtCompose.cs)
        DisposeDcSound(); // stop playback + end the audio thread (.Sound.cs)
        _services.ClientState.Logout       -= OnDcLogout;
        _services.ClientState.SceneChanged -= OnDcSceneChanged;
        // OFF at the Z.UIMgr instance WITHOUT touching the persisted config. The wrapper stays installed but passes
        // through, and a request still pending is handed to the game's own view (DcSetFlag).
        DcSetFlag(false);
    }

    private void ApplyDcReplace(bool on)
    {
        _dcReplace = on;
        _cfg.Set<bool>("dungeon_confirm_replace", on);
        _cfg.Save();
        DcSetFlag(on);
        if (!on && !_dcPreview) DcHide();   // the pending request (if any) just moved to the game's view
        if (!on) DcSoundStopAll();          // an open Preview stays up, but OFF = no sounds (.Sound.cs)
    }

    // Logout runs synchronously (Update is world-gated): hide now, drop the request. Its stash belongs to the old session.
    private void OnDcLogout() { DcHide(); _dcDismissed = true; }

    private void OnDcSceneChanged(string? _) => _dcScenePending = true;   // handled on the next tick (main thread)

    // ── tick ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private void DcTick()
    {
        TickOpacitySave();            // debounced settings opacity slider (.WindowOpacity.cs) — runs in every state
        DcRecentreOnCanvasChange();   // resolution / UI-scale change only, never after a drag — .Centre.cs
        if (_dcPreview)
        {
            DcTickEndState();
            if (_dcPreview && _dcEnd == DcEnd.None && DcRemaining <= 0) DcBeginTimedOut("");   // waiting rows → No answer
        }
        if (!_dcReplace) return;
        if (_services.ClientState.Phase != GamePhase.World || !_services.Lua.Ready)
        {
            if (_dc != null && !_dcPreview) { DcHide(); _dcDismissed = true; }
            return;
        }

        _services.Lua.DoString(DcPollChunk);
        var head = _services.Lua.ReadGlobalString("__stellarDCHead");
        if (head == null) return;
        if (head == "X") { DcSetFlag(true); return; }   // Z.UIMgr re-created (relog) — reinstall + re-enable
        var h = head.Split('|');
        if (h.Length < 4) return;
        string id = h[0];
        int seq = DcInt(h[1]);
        bool open = h[2] == "1", native = h[3] == "1";
        // Head tail (Lua.cs DcVoteHook / poll): who refused this request (charId, '' = unknown), the end kind
        // (R / C / T / ''), the drained vote queue — one diagnostic line per vote, to prove in game what arrived — then
        // the TimeOut and Agree voters (charIds joined by ',').
        string refuser = h.Length > 4 ? h[4] : "", endKind = h.Length > 5 ? h[5] : "", touts = h.Length > 7 ? h[7] : "";
        string agrees = h.Length > 8 ? h[8] : "";
        if (h.Length > 6 && h[6].Length > 0)
            foreach (var v in h[6].Split(';'))
            {
                var p = v.Split(':');
                if (p.Length >= 4) _services.Log.Info($"[DungeonConfirm] vote charId={p[0]} code={p[1]} ({p[2]}) seq={p[3]}");
            }

        if (id != _dcStashId || seq != _dcSeq)
        {
            // A new request (or our first look at this stash). A request that is already closed is history: don't show it.
            bool first = _dcSeq < 0 && _dcStashId.Length == 0;
            _dcStashId = id; _dcSeq = seq; _dcSeenAt = DcNow;
            _dcShownOnce = false; _dcConfirmed = false; _dcMatchDeclined = false; _dcArmUntil = 0;
            _dcEnd = DcEnd.None; _dcScenePending = false; _dcSilent.Clear(); _dcToutsApplied = "";
            _dcDismissed = !open || (first && seq == 0);
            // A real request always wins over a preview; a stale (closed) one leaves the preview alone.
            if (_dc != null && (!_dcPreview || open)) DcHide();
        }
        // TimeOut votes that land after our own countdown already ended the request replace the waiting-rows guess.
        if (_dcEnd == DcEnd.TimedOut && !_dcPreview && touts != _dcToutsApplied) DcMarkTimeouts(touts);
        if (_dcPreview) return;
        if (_dcScenePending)
        {
            _dcScenePending = false;
            // Entering the dungeon never goes through CloseView in every path, so a request seen well BEFORE this scene
            // change is over. One seen just now is the game's re-open after the scene change (team_vm.lua:1129) — keep it.
            if (open && !_dcDismissed && DcNow - _dcSeenAt > 2.0) { DcCloseStash(seq); DcHide(); _dcDismissed = true; return; }
        }
        if (_dcDismissed) return;
        if (native) { DcHide(); _dcDismissed = true; return; }   // fallback / OFF hand-off: the game's view owns it now

        if (open)
        {
            var snap = DcParse(_services.Lua.ReadGlobalString("__stellarDCSnap"));
            if (snap == null)
            {
                if (!_dcShownOnce && DcNow - _dcSeenAt > DcFallbackSec)
                {
                    _services.Log.Warning($"[DungeonConfirm] fallback — no valid snapshot for request {seq}: " +
                        (_services.Lua.ReadGlobalString("__stellarDCSnap") ?? "(nil)"));
                    DcOpenNative(seq);
                    _dcDismissed = true;
                }
                DcTickEndState();   // a failed read mid-hold must not stall the hold timer
                return;
            }
            DcApplySnap(snap);
            if (_dcEnd == DcEnd.None)
            {
                if (snap.FirstDeclined is { } d) DcBeginEnd(DcEnd.Declined, d.Me ? L("dc.you") : d.Name);
                else if (DcRemaining <= 0)
                {
                    // The native view self-closes at 0 too (team_enter_view.lua:342-346). If entry is already under
                    // way (everyone agreed / "doing") that's not a failure — just go.
                    DcCloseStash(seq);
                    if (snap.Doing || snap.ReadyCount == snap.Members.Count) { DcHide(); _dcDismissed = true; return; }
                    DcBeginTimedOut(touts);
                }
            }
        }
        else if (_dcEnd == DcEnd.None)
        {
            // The game closed it. A refuse wins (the server sends the vote, then state No closes the view before our
            // next poll, so the snapshot never saw it): mark that row Declined and name them. Then fold in the Agree
            // votes — the last ones often land in the same poll as the close, so the frozen _dc still shows them
            // waiting. Then: everyone ready (or the leader's entry already "doing") = we're going in, just hide. Then a
            // Cancel / TimeOut vote. With no failure signal at all: out of time = a timeout guess; time left = entry is
            // proceeding (a real leader cancel always sends a Cancel vote, code 3, so it's caught above), just hide.
            if (_dc != null && DcMarkRefuser(refuser) is { } r) DcBeginEnd(DcEnd.Declined, r.Me ? L("dc.you") : r.Name);
            else if (_dc == null || _dc.Doing || DcMarkAgrees(agrees) == _dc.Members.Count) { DcHide(); _dcDismissed = true; return; }
            else if (_dc.FirstDeclined is { } d) DcBeginEnd(DcEnd.Declined, d.Me ? L("dc.you") : d.Name);
            else if (endKind == "T") DcBeginTimedOut(touts);
            else if (endKind == "C") DcBeginEnd(DcEnd.Cancelled, "");
            else if (DcRemaining <= 0.5) DcBeginTimedOut(touts);
            else { DcHide(); _dcDismissed = true; return; }
        }
        DcTickEndState();
    }

    private void DcApplySnap(DcSnap s)
    {
        if (_dcEnd != DcEnd.None && _dc != null) return;   // freeze the picture during the end-state hold
        _dc = s;
        if (s.Twenty) DcBuildGrid(s); else DcFitAffixLabels(s);
        if (s.Me is { Ready: 1 }) _dcConfirmed = true;
        if (!_dcShownOnce) { _dcShownOnce = true; DcShow(); DcSoundOnOpen(); }   // once per request (.Sound.cs)
    }

    // The refuser's row, set to Declined (the window's rows read _dc.Members, frozen from here on). Name from the member
    // list we already have; a charId not in it (or none) → null, and the end state falls back to the generic text.
    private DcMember? DcMarkRefuser(string charId)
    {
        if (charId.Length == 0 || _dc == null) return null;
        foreach (var m in _dc.Members)
            if (m.CharId == charId) { m.Ready = 2; return m; }
        _services.Log.Info($"[DungeonConfirm] refuser {charId} not in the member list — generic end text");
        return null;
    }

    // Closed branch only (_dc is frozen there, so no later snapshot undoes it): set Ready = 1 on every row with an Agree
    // vote for this request (Lua head 'agrees'). A Declined row (2) stays declined. Returns the new ReadyCount.
    private int DcMarkAgrees(string charIds)
    {
        if (_dc == null) return 0;
        if (charIds.Length > 0)
        {
            var ids = charIds.Split(',');   // ≤ 20 members — a linear scan is fine
            foreach (var m in _dc.Members)
                if (m.Ready != 2 && Array.IndexOf(ids, m.CharId) >= 0) m.Ready = 1;
        }
        return _dc.ReadyCount;
    }

    private void DcBeginTimedOut(string touts) { DcBeginEnd(DcEnd.TimedOut, ""); DcMarkTimeouts(touts); }

    private void DcBeginEnd(DcEnd kind, string name)
    {
        _dcEnd = kind; _dcEndName = name; _dcEndCount = 0; _dcEndUntil = DcNow + DcHoldSec;
        DcSoundStopAll();   // the request is over: the open sound (a long custom one too) ends here
    }

    private void DcTickEndState()
    {
        if (_dcEnd != DcEnd.None && DcNow >= _dcEndUntil) { DcHide(); _dcDismissed = true; }
    }

    // ── button handlers (preview: every button only closes) ──────────────────────────────────────────────────────
    private void DcClose() { DcHide(); _dcDismissed = true; }

    private void DcOnConfirm()
    {
        if (_dcPreview) { DcClose(); return; }
        if (_dc == null || _dcEnd != DcEnd.None || _dcConfirmed) return;
        _dcConfirmed = true;
        DcSoundStopAll();   // answered: the open sound stops with the ticks (.Sound.cs)
        if (_dc.Match) DcLuaMatchReady(_dcSeq, true); else DcLuaReply(_dcSeq, true);
    }

    private void DcOnDecline()
    {
        if (_dcPreview) { DcClose(); return; }
        if (_dc == null || _dcEnd != DcEnd.None) return;
        if (_dc.Match)
        {
            // Our own confirm step instead of the game's YesNo dialog: first click arms "Decline?" for DcArmSec.
            if (_dcMatchDeclined) return;
            if (DcNow > _dcArmUntil) { _dcArmUntil = DcNow + DcArmSec; return; }
            _dcArmUntil = 0; _dcMatchDeclined = true;
            DcSoundStopAll();
            DcLuaMatchReady(_dcSeq, false);
            return;
        }
        DcLuaReply(_dcSeq, false);   // + CloseEnterView, like the native button
        DcClose();
    }

    private void DcOnLeaderCancel()
    {
        if (_dcPreview) { DcClose(); return; }
        if (_dc == null || _dcEnd != DcEnd.None || _dc.Doing) return;
        DcLuaLeaderCancel(_dcSeq);   // + CloseEnterView, like the native button
        DcClose();
    }
}
