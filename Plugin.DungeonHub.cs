using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — one window to reach every dungeon: pick a dungeon, pick a difficulty, then Solo / Match / Party.
// User-approved design 2026-11-06 (prototype _scratch\prototypes\dungeon-hub-v3.html), built at Dungeon Confirm's 1.6×
// scale. Game facts: Knowledge Base\Dungeon-Access-Hub.md (§7 per-difficulty modes, §8 this build's mapping).
//
// Files: this one = lifecycle, the poll tick, the page-open state machine, the button actions and the centring.
// .Lua = every Lua chunk. .Model = the parsed snapshot + selection + derived text. .Window = scale, header, tabs, list,
// bottom bar. .Detail = the right-hand panel and the three "how to go" buttons. .Art = thumbnails / art band.
//
// What the buttons do (never raw BeginMatch):
//   Match  → the game's own RequestBeginMatch (all its checks and dialogs). Cancel → its leader check + AsyncCancelMatch.
//   Solo / Party → ENTER directly with the page Enter button's exact AsyncCreateLevel call + its prechecks (.Enter.cs).
//          Fallback only (unexpected data): open the game's dungeon page with this dungeon / difficulty / mode
//          preselected (hero: replay the NPC state; raid: OpenRaidMainView(group, diff)) and hide the hub, so the
//          player presses the game's own Enter.
//          No page within DhOpenWaitSec → walk to the dungeon's NPC (quick_jump), as the game's own "go there".
// Every open attempt and its outcome is logged "[DungeonHub] …" for in-game validation.
public sealed partial class Plugin
{
    private const double DhPollSec = 0.5, DhOpenWaitSec = 4.0;
    private const int DhFullEvery = 6;   // catalog (unlocks, counters, list) every 6th poll = 3 s

    private IWindowControl _dhWin = null!;
    private IDisposable? _dhHotkey, _dhTick;
    private INativeUiElementHandle? _dhRailButton;
    private bool _dhShow, _dhFullDue = true;
    private int _dhPolls;
    private double _dhFullAt;   // one extra FULL poll at this DcNow (after the Unstable refresh on open); 0 = none

    // Page open in flight: what we asked for (for the log + the NPC fallback) and since when.
    private bool   _dhOpening;
    private string _dhOpenWhat = "", _dhOpenLast = "";
    private double _dhOpenSince;
    private int    _dhOpenDungeon, _dhOpenDiff;

    private void InitDungeonHub()
    {
        _dhTab = Math.Clamp(_cfg.Get<int>("dungeon_hub_tab", 0), 0, DhTabCount - 1);
        DhBuildLiveText();       // the header / bar texts before the first poll ("Solo", "Not in a party")
        _dhWin = DhRegister();   // Plugin.DungeonHub.Window.cs
        // Registered UNBOUND (null default key) — rebindable in the hotkey settings, the user assigns the key.
        _dhHotkey = _services.Hotkeys.DeclareAction(
            new HotkeyAction("dungeonhub.hub-toggle", L("dh.hotkey.toggle"), null),
            callback: () => { if (_services.ClientState.Phase == GamePhase.World) { if (_dhShow) DhHide(); else DhOpen(); } });
        _services.ClientState.Logout += OnDhLogout;
        _dhTick = _services.Framework.Every(TimeSpan.FromSeconds(DhPollSec), DhTick);
        // "Dungeon Hub" on the game's Esc-menu right rail (user-approved icon _scratch\prototypes\esc-dungeons-icon\
        // dungeons_v2b.png). The framework only injects the rail in-world; the phase check mirrors the hotkey anyway.
        // Click always OPENS (DhOpen fronts an already-visible hub) — the Esc menu may stay up behind it.
        _dhRailButton = _services.NativeUi.Register(new MenuButtonSpec(
            NativeUiAnchor.MainMenuRail, L("dh.title"), IconKey: null, Tooltip: L("dh.rail.tooltip"),
            OnClick: () => { if (_services.ClientState.Phase == GamePhase.World) DhOpen(); },
            IconPng: CachedPng("dungeons-esc-icon.png")));
    }

    private void DisposeDungeonHub()
    {
        _dhTick?.Dispose();
        _dhHotkey?.Dispose();
        _dhRailButton?.Remove();   // the handle isn't IDisposable — Remove() pulls it and stops re-injection
        DhStopCentring();
        DhReleaseArt();
        _services.ClientState.Logout -= OnDhLogout;
    }

    // Logout runs synchronously (Update is world-gated): hide now and forget the open in flight.
    private void OnDhLogout() { DhHide(); _dhOpening = false; _dhHaveCatalog = false; DhWarmRearm(); }

    // Every open lands centred and in front. Fronting on EVERY open is the house OpenCentred rule: ZFront is sticky
    // (WindowBuilder-Patterns.md), and this plugin already fronts the Dungeon Confirm windows on each show.
    private void DhOpen()
    {
        DhWarmEnd();   // a real open takes over (no-op once the warm-up finished)
        _dhShow = true;
        _dhFullDue = true;
        _dhWin.SetVisible(true);
        _dhWin.BringToFront();
        DhStartCentring();
        // today's Unstable set: ask the server again (async RPC); the catalog re-reads it on a FULL poll ~1.5 s later
        if (_services.Lua.Ready) { _services.Lua.DoString(DhRefreshUnstableChunk); _dhFullAt = DcNow + 1.5; }
        DhWbFetch();   // this week's World Boss (art / name, and match_activity_vm needs it before a match)
        DhPoll();   // fill at once when Lua is ready (a first open otherwise shows an empty list for 0.5 s)
    }

    private void DhHide()
    {
        _dhShow = false;
        _dhWin.SetVisible(false);
        DhStopCentring();
        DhReleaseArt();   // no composed raid art kept while hidden (.Art.cs)
    }

    // ── tick: poll only while the window is up, or while a page open is still being followed ──────────────────────
    private void DhTick()
    {
        DhRecentreOnCanvasChange();
        DhFollowEnter();   // the direct enter's RPC result (.Enter.cs), followed while hidden too
        DhCmFollow();      // a commission Accept / Teleport RPC result (.Commissions.cs)
        DhWarmTick();      // the first-open warm-up after entering the world, one stage per tick (.Prewarm.cs)
        if (!_dhShow && !_dhOpening) return;
        DhPoll();
    }

    // warm: the hidden warm-up's FULL poll (.Prewarm.cs) — a catalog read while the window is hidden, which a normal
    // hidden poll (following a page open) never does.
    private void DhPoll(bool warm = false)
    {
        if (_services.ClientState.Phase != GamePhase.World || !_services.Lua.Ready) return;
        bool due = _dhFullAt > 0 && DcNow >= _dhFullAt;
        if (due) _dhFullAt = 0;
        bool full = _dhFullDue || due || !_dhHaveCatalog || ++_dhPolls % DhFullEvery == 0;
        _dhFullDue = false;
        full = full && (_dhShow || warm);
        var cur = _dhTab == DhTabHero ? DhCur : null;
        _services.Lua.DoString(DhPollChunk(full, cur?.MasterId ?? 0));
        var raw = _services.Lua.ReadGlobalString("__stellarDHSnap");
        // one-shot season diagnostic (.Lua.Catalog.cs, the CN season-4 empty-list hunt): parked once per Lua state on
        // the first FULL poll; read before the ERR check so a catalog failure after it still gets logged. Off the hot
        // path: only FULL polls, and only until logged once.
        if (full && !_dhDiagLogged)
        {
            var diag = _services.Lua.ReadGlobalString("__stellarDHDiag");
            if (diag != null)
            {
                _dhDiagLogged = true;
                _services.Log.Info($"[DungeonHub] season diag: {diag}");
            }
        }
        if (raw == null) return;
        if (raw.StartsWith("ERR", StringComparison.Ordinal))
        {
            if (raw != _dhLastErr) _services.Log.Warning($"[DungeonHub] poll failed: {raw}");
            _dhLastErr = raw;
            return;
        }
        _dhLastErr = "";
        DhParse(raw);
        DhFollowOpen();
    }

    private string _dhLastErr = "";
    private bool _dhDiagLogged;

    // The page open in flight, from the poll's openState: applied / failed → log and done; still no page after
    // DhOpenWaitSec → walk to the NPC; page up but our preselect never applied → log and leave the page as it is.
    private void DhFollowOpen()
    {
        if (!_dhOpening) return;
        string s = _dhLive.OpenState;
        if (s.StartsWith("A", StringComparison.Ordinal) || s.StartsWith("E", StringComparison.Ordinal))
        {
            _services.Log.Info($"[DungeonHub] {_dhOpenWhat}: {(s[0] == 'A' ? "page open, preselected —" : "preselect failed —")} {s.Substring(1).Trim()}");
            _dhOpening = false;
            return;
        }
        if (s != _dhOpenLast) { _dhOpenLast = s; if (s == "P") _services.Log.Info($"[DungeonHub] {_dhOpenWhat}: page open, waiting for it to settle"); }
        if (DcNow - _dhOpenSince < DhOpenWaitSec) return;
        _dhOpening = false;
        if (s == "P")
        {
            _services.Log.Warning($"[DungeonHub] {_dhOpenWhat}: page open but the preselection never applied — left as the game opened it");
            _services.Lua.DoString(DhClearPendChunk);
            return;
        }
        _services.Log.Warning($"[DungeonHub] {_dhOpenWhat}: no page after {DhOpenWaitSec:0} s (state '{s}') — walking to the NPC");
        DhWalk();
    }

    private void DhWalk()
    {
        _services.Lua.DoString(DhWalkChunk(_dhOpenDungeon, _dhOpenDiff));
        _services.Log.Info($"[DungeonHub] {_dhOpenWhat}: walk-to-NPC → {_services.Lua.ReadGlobalString("__stellarDHOpen") ?? "(no result)"}");
    }

    // Runs an open chunk, logs what the game said, and starts following it (followPage = a pend was left for the poll).
    private void DhRunOpen(string what, string chunk, int dungeonId, int diff, bool followPage)
    {
        if (!_services.Lua.Ready) { _services.Log.Warning($"[DungeonHub] {what}: Lua not ready"); return; }
        _services.Lua.DoString(chunk);
        var res = _services.Lua.ReadGlobalString("__stellarDHOpen") ?? "(no result)";
        _services.Log.Info($"[DungeonHub] {what}: {res}");
        if (res.StartsWith("ERR", StringComparison.Ordinal) || !followPage) return;
        _dhOpenWhat = what; _dhOpenDungeon = dungeonId; _dhOpenDiff = diff;
        _dhOpenSince = DcNow; _dhOpenLast = ""; _dhOpening = true;
        DhHide();   // the game's page is where the player continues (its own Enter); the hub would cover it
        if (res.EndsWith("= false", StringComparison.Ordinal))
        {
            // GoToFunc refused (scene / buff / dead check, with the game's own tip) — no page will come.
            _dhOpening = false;
            _services.Lua.DoString(DhClearPendChunk);
            _services.Log.Warning($"[DungeonHub] {what}: the game refused to open the page — walking to the NPC");
            DhWalk();
        }
    }

    // ── button actions ────────────────────────────────────────────────────────────────────────────────────────────
    // Solo / Party enter directly (.Enter.cs); DhOnPage stays as its fallback when the recipe meets unexpected data.
    // Guild Hunt enters too, with its own recipe (.GuildHunt.cs).
    private void DhOnSolo() { if (DhIsGh(DhCur)) DhOnGhEnter(solo: true); else DhOnEnter(solo: true); }
    private void DhOnParty() { if (DhIsGh(DhCur)) DhOnGhEnter(solo: false); else DhOnEnter(solo: false); }

    private void DhOnPage(bool solo)
    {
        var e = DhCur; var lv = DhCurLevel;
        if (e == null || lv == null || !DhBtnState(solo ? 0 : 2).Enabled) return;
        string mode = solo ? "solo" : "party";
        if (e.Tab == DhTabRaid)
        {
            int diff = _dhLevelIdx + 1;
            DhRunOpen($"open raid group={e.Group} diff={diff} mode={mode} ({e.Name})",
                DhOpenRaidChunk(e.Group, diff, solo), lv.DungeonId, 0, followPage: true);
            return;
        }
        if (DhIsUnstable)
        {
            DhRunOpen($"open unstable dungeon={lv.DungeonId} mode={mode} ({e.Name})",
                DhOpenUnstableChunk(lv.DungeonId, solo), lv.DungeonId, 0, followPage: true);
            return;
        }
        int level = _dhLevelIdx, md = DhIsMaster ? _dhMasterDiff : 0;
        DhRunOpen($"open hero group={e.Group} level={level} master={md} mode={mode} ({e.Name}, dungeon {lv.DungeonId})",
            DhOpenHeroChunk(e.Group, level, md, solo), lv.DungeonId, md, followPage: true);
    }

    private void DhOnMatch()
    {
        var e = DhCur;
        if (e == null || !DhBtnState(1).Enabled) return;
        if (e.IsActivity)
        {
            if (DhIsWb(e)) DhOnWbMatch(e);   // the only activity with a match (.WorldBoss.cs)
            return;
        }
        var lv = DhCurLevel;
        if (lv == null) return;
        DhRunOpen($"match dungeon={lv.DungeonId} diff={lv.Diff} ({e.Name} {lv.Label})",
            DhMatchTeamChunk(lv.DungeonId, lv.Diff, noDiff: e.Tab == DhTabRaid || DhIsUnstable), 0, 0, followPage: false);
    }

    private void DhOnCancel() => DhRunOpen("cancel match", DhCancelChunk, 0, 0, followPage: false);

    private void DhOnActivityOpen()
    {
        var e = DhCur;
        if (e == null || !e.IsActivity || e.Locked) return;
        DhRunOpen($"open activity func={e.FuncId} ({e.Name})", DhOpenFuncChunk(e.FuncId), 0, 0, followPage: false);
        DhHide();
    }

    // ── centring: every open lands dead centre, then the window is the user's to drag (Dungeon Confirm's recipe, ──
    // Plugin.DungeonConfirm.Centre.cs: re-centre each plugin tick for DcCentreFrames ticks after the show — the first
    // SetRect is a no-op until mounted and the mount restores the saved rect — then only on a canvas size change).
    // The burst ends the moment the window moves under us (a user drag — DcUserMoved): before that check, every burst
    // tick dragged it back to centre, so the first drag after an open snapped back (user report 2026-11-08).
    private int   _dhCentreLeft;
    private bool  _dhCentreSub;
    private float _dhCentreCw, _dhCentreCh;
    private (float X, float Y)? _dhPlaced;   // where we last left the mounted window (null = not mounted / not placed yet)

    private void DhStartCentring()
    {
        _dhPlaced = null;
        DhCentreNow();
        _dhCentreLeft = DcCentreFrames;
        if (!_dhCentreSub) { _services.Framework.Update += OnDhCentreFrame; _dhCentreSub = true; }
    }

    private void DhStopCentring()
    {
        _dhCentreLeft = 0;
        if (_dhCentreSub) { try { _services.Framework.Update -= OnDhCentreFrame; } catch { } _dhCentreSub = false; }
    }

    private void OnDhCentreFrame(float _)
    {
        if (--_dhCentreLeft < 0 || !_dhShow) { DhStopCentring(); return; }
        if (DcUserMoved(_dhWin, _dhPlaced)) { DhStopCentring(); return; }   // dragged → the user's now
        DhCentreNow();
    }

    private void DhRecentreOnCanvasChange()
    {
        if (_dhCentreSub || !_dhShow || _dhCentreCw <= 1f) return;
        float cw = _services.Framework.CanvasWidth, ch = _services.Framework.CanvasHeight;
        if (Math.Abs(cw - _dhCentreCw) >= 0.5f || Math.Abs(ch - _dhCentreCh) >= 0.5f)
            DhCentreNow();
    }

    private void DhCentreNow()
    {
        if (!_dhShow) return;
        float cw = _services.Framework.CanvasWidth, ch = _services.Framework.CanvasHeight;
        if (cw <= 1f || ch <= 1f) return;
        _dhCentreCw = cw; _dhCentreCh = ch;
        var r = _dhWin.Rect;
        float h = r.Height > 1f ? r.Height : DhH;
        float x = MathF.Max(0f, MathF.Floor((cw - DhW) / 2f)), y = MathF.Max(0f, MathF.Floor((ch - h) / 2f));
        if (!(Math.Abs(r.X - x) < 0.5f && Math.Abs(r.Y - y) < 0.5f && Math.Abs(r.Width - DhW) < 0.5f))
            _dhWin.SetRect(new WindowRect(x, y, DhW, h));
        _dhPlaced = DcPlacedPos(_dhWin);   // the live (on-screen-clamped) pose, read back after the set
    }
}
