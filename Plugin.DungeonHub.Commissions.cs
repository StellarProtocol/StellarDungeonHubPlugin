using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — Bureau Commissions (Other tab, first row; FunctionID 800100 = E.FunctionID.WorldEvent), the daily
// commission. Approved mock _scratch\prototypes\dungeon-hub-commissions.html. Game facts: Dungeon-Access-Hub.md §11.
// Paths below are under Resources\release_3.7\lua\lua.
//   data    quest held = worldquest.CheckHasWorldQuest() (worldquest_vm.lua:97-107: questList.questMap[Z.Global.
//           WorldEventQuestId = 120001] ~= nil); accepts left = CharSerialize.worldEventMap.acceptCount (rolls over up to
//           Z.Global.WorldEventDaysLimit 3); today's events = worldEventMap.eventMap {id, targetNum, award}, sorted by id,
//           done = award == 1, counted against Z.Global.WorldEventDailyCount 3 — the board's own count
//           (worldquest_interactive_window_view.lua:91-138). Name / Scene / DungeonId = DailyWorldEventTable[id]; area =
//           SceneTable[Scene].Name (worldquest_main_sub_view.lua:154-162).
//   accept  the board's acceptWorldQuest (worldquest_interactive_window_view.lua:175-193): refused when the quest is
//           already held or acceptCount == 0, then quest.AsyncAcceptQuest(120001) (an RPC: ShowTips(ret) + false on a
//           server code, quest_vm.lua) and tip 1381016 on success. Skipped: its OpenNormalDialog confirm (our press is
//           explicit) and the success jump to the world map (OpenWorldQuestWorldMap — the hub lists the events itself).
//   board   "Go to the board" = the game's own "go there" for this activity: the recommended-play row's QuickJumpType /
//           QuickJumpParam (SeasonActTable row 10, season 3: TraceSceneTarget [8 Asterleeds, TrackType.SceneObject 5,
//           4361]) through quick_jump.DoJumpByConfigParam — what recommendedplay_main_view.lua:96 runs for its Go.
//   teleport Enter's fallback only — the event panel's btn_transfer (worldquest_main_sub_view.lua:49-56):
//           worldquest.WorldQuestTransfer(eventId, token) (= world_proxy.WorldEventTransfer, tips on failure) then
//           GotoMainView on success.
//   enter   its btn_enter (:72-97): ui_enterdungeonscene AsyncCreateLevel(800100, DungeonId, token) — the decompile reads
//           dungeonsTable.FusanctionID (a glitch), the playType is the row's FunctionID 800100. Via Enter.cs DhEnterWrap.
//           A server code (≠ 0, ≠ nil) → falls back to Teleport for that event; nil (Condition refused, tip shown) and
//           the 15 s timeout do not (Guild Hunt's rule).
//   rows    Enter is the ONLY row button (user 2026-11-07: Teleport / Party buttons removed); Teleport survives only as
//           Enter's fallback.
//   resets  Z.Global.WorldEventDailyRefreshTimer (15): TimeTools.GetNextRefreshRemainingTime (the timer's TimerInfo.
//           nextTimeStamp, what the daily-quest pages count down to), else GetLeftTimeByTimerId's start / end. Read every
//           30 s (cached on st), ticked locally; shown as a local clock time + "in Xh Ym".
//   art     the activity row's own (catalog act(): recommended-play SeasonActTable BackGroundPic
//           ui/background/seasonact/activity_weituo) — no commission-specific art code.
// C record (every poll, after G): haveQuest, acceptLeft, total, cap, resetSec, resetHow, n, (id, name, area, done,
// dungeonId) × n.
public sealed partial class Plugin
{
    // The game offers 6 events a day (any 3 complete the commission); DhCmSlots covers them all — no "+N more".
    private const int DhCmFunc = 800100, DhCmSlots = 6;

    private sealed class DhCmEvent { public int Id, DungeonId; public string Name = "", Area = ""; public bool Done; }

    // Rules (user + board, Dungeon-Access-Hub.md §11): DoneCount is the RAW award==1 count (it can pass 3 after a second
    // round — the board shows it raw too). The board's Accept is shown whenever the quest isn't held and is only gated
    // by acceptCount — it does NOT depend on the event list (events can exist, even all done, with the quest not held).
    // After a finished round, ENTERING an unfinished event auto-accepts the next round from a stacked accept, so Enter
    // stays usable whenever the quest is held OR an accept is left.
    private sealed class DhCmState
    {
        public bool Have;
        public int AcceptLeft, Total = 3, Cap = 3, ResetSec = -1, DoneCount;
        public double At;                     // DcNow of the poll (the reset countdown ticks locally from there)
        public string ResetHow = "", ResetClock = "";
        public readonly List<DhCmEvent> Events = new();
        public bool AllDone => DoneCount >= Total && Total > 0;
        public bool NotAccepted => !Have && DoneCount == 0;          // the amber "Not accepted" state
        public bool CanAccept => !Have && AcceptLeft > 0;            // the board's Accept (visible + enabled)
        public bool CanEnter => Have || AcceptLeft > 0;              // entering auto-accepts from a stacked accept
    }

    private DhCmState? _dhCm;
    private string _dhCmLogged = "";
    private bool _dhEnterCm;                   // the enter being followed (Enter.cs DhFollowEnter) is a commission one
    private int _dhCmEnterEvent;
    // Accept / Teleport: an RPC in a coroutine, its result parked in __stellarDHCmRet, followed on the hub tick
    private bool _dhCmWait;
    private double _dhCmSince;
    private string _dhCmWhat = "";

    private static bool DhIsCm(DhEntry? e) => e is { IsActivity: true, FuncId: DhCmFunc };

    private void DhParseCm(string[] f)
    {
        var c = new DhCmState
        {
            Have = f[1] == "1", AcceptLeft = DcInt(f[2]), Total = Math.Max(1, DcInt(f[3], 3)), Cap = Math.Max(1, DcInt(f[4], 3)),
            ResetSec = DcInt(f[5], -1), ResetHow = f[6], At = DcNow,
        };
        int n = DcInt(f[7]);
        for (int i = 0; i < n && 12 + 5 * i < f.Length; i++)
        {
            var ev = new DhCmEvent
            {
                Id = DcInt(f[8 + 5 * i]), Name = f[9 + 5 * i], Area = f[10 + 5 * i], Done = f[11 + 5 * i] == "1",
                DungeonId = DcInt(f[12 + 5 * i]),
            };
            c.Events.Add(ev);
            if (ev.Done) c.DoneCount++;
        }
        // a local clock time for the reset, fixed per poll (the countdown under it ticks from At)
        if (c.ResetSec > 0) c.ResetClock = DateTime.Now.AddSeconds(c.ResetSec).ToString("HH:mm");
        _dhCm = c;
        string sig = $"quest={c.Have} acceptLeft={c.AcceptLeft}/{c.Cap} done={c.DoneCount}/{c.Total} events=" +
                     string.Join(",", c.Events.ConvertAll(x => $"{x.Id}{(x.Done ? "*" : "")}"));
        if (sig != _dhCmLogged)
        {
            _dhCmLogged = sig;
            _services.Log.Info($"[DungeonHub] commissions: {sig} reset={c.ResetSec}s via '{c.ResetHow}' ({c.ResetClock})");
        }
    }

    // ── text ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private string DhCmDone() => _dhCm is { } c ? $"{c.DoneCount} / {c.Total} done" : "—";

    // List sub-line: Not accepted · N left · D / 3 done · green 3 / 3 done ("Locked" comes first, DhRowSub). The green is
    // a TMP <color> tag — the sub-line is a RichEmoji TextElement (as DhRaidRowSub).
    private string DhCmRowSub(DhEntry e) => _dhCm switch
    {
        null => e.SubText,
        { NotAccepted: true } c => $"Not accepted · {c.AcceptLeft} left",
        { AllDone: true } => $"<color=#5fd38d>{DhCmDone()}</color>",
        _ => DhCmDone(),
    };

    private string _dhCmCd = ""; private int _dhCmCdSec = -1;
    private string DhCmResetIn()
    {
        var c = _dhCm;
        if (c == null || c.ResetSec <= 0) return "";
        int s = Math.Max(0, (int)(c.ResetSec - Math.Max(0, DcNow - c.At)));
        if (s == _dhCmCdSec) return _dhCmCd;
        _dhCmCdSec = s;
        int h = s / 3600, m = s % 3600 / 60, sec = s % 60;
        return _dhCmCd = h > 0 ? $"in {h}h {m:00}m" : m > 0 ? $"in {m}m {sec:00}s" : $"in {sec}s";
    }

    // ── panel: art · name · three tiles · the events in one fixed-height block, so the window is the same height as on
    // every other tab (v5 list-shift rule: a strut beside a top-pinned Conditional).
    // Block = the Accept bar (or a muted status line in the same reserved height) · the event list, NO scroll (user
    // 2026-11-07: the "Daily · <areas>" sub-line and the "TODAY'S EVENTS · ANY 3" caption were dropped — the Today tile
    // has the count). Worst case = bar 42 + next-round hint 26 + six 50 px rows + 6 px gaps = 410; the block is 343 at
    // the 932 px window, so Window.cs DhCmRoom (+74, every tab) makes it 417.
    // Sizes are computed (=>) from Window.cs's fields at build time, never in a static initialiser (partial-file order).
    private static float DhCmBlockH => DhColsH - 2f * DhPanPadY - DhArtH - DhNameH - DhGhTileH - 3f * DhPanGap;
    private static float DhCmRowH => DcSE(31f);       // 50 (was 54 — 4 px a row is what lets six fit in ≤ 80 px more)
    private static float DhCmPillH => DcSE(26f);      // 42
    private static float DhCmPillW => DcSE(56f);      // 90
    private static float DhCmBoardW => DcSE(112f);    // 180
    private static float DhCmRowGap => DcS(4f);       // 6
    private static float DhCmRowW => DhDetInW;        // no scroll viewport → no bar lane

    private HudElement DhCmPanel() => DhPanelNoCaption(
        DhArtBand(),
        DhNameRow(),
        new RowElement(new HudElement[]
        {
            DhGhTile(() => "Today", DhCmTodayText, DhCmTodayColor, null, DhThirdW),
            DhGhTile(() => "Accepts left", () => _dhCm is { } c ? $"{c.AcceptLeft} / {c.Cap}" : "—",
                () => _dhCm is { AcceptLeft: <= 0 } ? MutedColor() : TextColor(), null, DhThirdMidW),
            DhGhTile(() => "Resets", () => _dhCm is { ResetClock.Length: > 0 } c ? c.ResetClock : "—", () => TextColor(),
                DhCmResetIn, DhThirdW),
        }, Gap: DhPanGap),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhCmBlockH),
            new CellElement(DhTopPinned(new ColumnElement(new HudElement[]
            {
                DhCmBar(),
                DhCmEventList(),
            }, Gap: DhCmRowGap)), Width: DhDetInW),
        }, Gap: 0.01f));

    private string DhCmTodayText() => DhCur is { Locked: true } ? "Locked" : _dhCm switch
    {
        null => "—",
        { NotAccepted: true } => "Not accepted",
        _ => DhCmDone(),
    };

    private ColorRgba DhCmTodayColor() => DhCur is { Locked: true } ? MutedColor() : _dhCm switch
    {
        null => MutedColor(),
        { NotAccepted: true } => DhAmber,
        { AllDone: true } => DhGreen,
        _ => TextColor(),
    };

    // The Accept bar (quest not held + an accept left): Accept (accent, fills) + Go to the board (neutral). Otherwise the
    // same reserved height holds a quiet status line ("Quest in progress" / "No accepts left" + the board / "Locked").
    private HudElement DhCmBar() => new ConditionalElement(() => DhCur is { Locked: false } && _dhCm is { CanAccept: true },
        new RowElement(new HudElement[]
        {
            DhPill(() => _dhCm is { } c ? $"Accept quest · {c.AcceptLeft} accept{(c.AcceptLeft == 1 ? "" : "s")} left" : "Accept quest",
                () => Menu.MenuAccent, () => DcOn(White, Menu.MenuAccent), DhOnCmAccept, DhDetInW - DhCmBoardW - DhPanGap, DhCmPillH),
            DhCmBoardPill(),
        }, Gap: DhPanGap),
        new ConditionalElement(() => DhCur is { Locked: false } && _dhCm is { Have: false },
            new RowElement(new HudElement[] { DhCmStatus(DhDetInW - DhCmBoardW - DhPanGap), DhCmBoardPill() }, Gap: DhPanGap),
            DhCmStatus(DhDetInW)));

    private HudElement DhCmBoardPill() =>
        DhPill(() => "Go to the board", () => DcNeutral(), () => EnsureContrast(TextColor(), DcNeutral(), 4.5f), DhOnCmBoard, DhCmBoardW, DhCmPillH);

    private HudElement DhCmStatus(float w) => new CellElement(new RowElement(new HudElement[]
    {
        new SpacerElement(DcSE(4f), DhCmPillH),
        new CellElement(new TextElement(() => DhCur is { Locked: true } ? "Locked" : _dhCm switch
            {
                null => "",
                { Have: true } => "Quest in progress",
                _ => "No accepts left · Enter is off until the reset",
            }, Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub) { Ellipsize = true }, Weight: 1f),
    }, Gap: 0.01f), Width: w);

    // Today's events: every event (up to DhCmSlots), sorted by id like the board (the poll sorts). On top, only when it
    // applies: the empty-list hint (no rows then, so it may wrap), or — round finished, quest not held, an accept left —
    // the next-round hint, one line on a DhActSubH strut (DhCmBlockH budgets it). Else-less Conditionals collapse
    // entirely (no column gap) while hidden (WindowBuilder.Layout.cs BuildConditional).
    private HudElement DhCmEventList()
    {
        var items = new HudElement[DhCmSlots + 2];
        items[0] = new ConditionalElement(() => _dhCm is { Events.Count: 0 }, DhCmHint(() =>
            "Today's events appear after you accept the daily quest at the Pioneer Bureau board."));
        items[1] = new ConditionalElement(() => _dhCm is { AllDone: true, CanAccept: true } && DhCur is { Locked: false },
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DhActSubH),
                new CellElement(new TextElement(() => "Entering an event also starts the next round.", Color: () => MutedColor(),
                    NoWrap: true, FontSize: DhFsSub) { Ellipsize = true }, Weight: 1f),
            }, Gap: 0.01f));
        for (int i = 0; i < DhCmSlots; i++)
        {
            int k = i;
            items[i + 2] = new ConditionalElement(() => DhCmEv(k) != null,
                new ConditionalElement(() => DhCmEv(k) is { Done: true }, DhCmEventRow(k, done: true), DhCmEventRow(k, done: false)));
        }
        return new ColumnElement(items, Gap: DhCmRowGap);
    }

    private HudElement DhCmHint(Func<string> text) =>
        new TextElement(text, Color: () => MutedColor(), Width: DhCmRowW, FontSize: DhFsSub) { RichEmoji = true };

    private DhCmEvent? DhCmEv(int i) => _dhCm is { } c && i < c.Events.Count ? c.Events[i] : null;

    // name over area · Enter (Solo blue, right) — the only button (user 2026-11-07: Teleport / Party removed; a server
    // refusal still falls back to the game's Teleport, DhCmAfterResult). Done = a dimmed tile with a green "Done".
    // The two looks are WHOLE bubbles swapped by a Conditional (never a Conditional inside a bubble's measured child).
    // The name cell (Weight 1, min 0, ellipsized) takes ALL the slack, so Enter / Done sit at the right edge with the
    // same 16 px right inset as the 16 px lead. ⚠️ The trailing spacer must never be 0 wide: a SpacerElement(0, …) is a
    // FLEXIBLE spacer, and it split the leftover with the name cell — the 06ad275 Enter sat at name + half the slack,
    // so its x moved with the name's width (Done rows had a 4 px fixed spacer and were right).
    private HudElement DhCmEventRow(int i, bool done)
    {
        Func<ColorRgba> ink = () => done ? Mix(PanelColor(), TextColor(), 0.6f) : TextColor();
        var parts = new List<HudElement>
        {
            new SpacerElement(DcSE(10f), DhCmRowH),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => DhCmEv(i)?.Name ?? "", Color: () => ink(), NoWrap: true, FontSize: DhFsName) { Bold = true, Ellipsize = true },
                new TextElement(() => DhCmEv(i)?.Area ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub) { Ellipsize = true },
            }, Gap: 0.01f), Weight: 1f),
        };
        if (done)
            parts.Add(DcBold(() => "Done", () => DhGreen, DhFsSmall));
        else
            parts.Add(DhCmPill(() => "Enter", () => DhSoloFill, () => DhSoloInk,
                () => DhCur is { Locked: false } && _dhCm is { CanEnter: true }, () => DhOnCmEnter(i), DhCmPillW));
        parts.Add(new SpacerElement(DcSE(10f) - DcSE(6f), 0f));   // + the 10 px row gap = 16, both looks
        return new BubbleElement(new RowElement(parts.ToArray(), Gap: DcSE(6f)))
        {
            FillColorFn = () => done ? Mix(PanelColor(), DcTile(), 0.55f) : DcTile(),
            CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhCmRowW,
        };
    }

    // A DhCmPillH pill (Window.cs DhPill) or its static dimmed twin, swapped whole.
    private HudElement DhCmPill(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Func<bool> enabled, Action onClick, float w) =>
        new CellElement(new ConditionalElement(enabled,
            DhPill(label, fill, text, onClick, w, DhCmPillH),
            new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, DhCmPillH),
                    DcDrop(DcBold(label, () => Mix(PanelColor(), TextColor(), 0.4f), DhFsSmall, w, TextAlign.Center)),
                }, Gap: 0.01f, Justify: RowJustify.Center))
                {
                    FillColorFn = () => Mix(PanelColor(), DcNeutral(), 0.55f), CornerRadius = DhCmPillH / 2f - 1f,
                    PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
                }, Width: w)), Width: w);

    // ── actions ───────────────────────────────────────────────────────────────────────────────────────────────────
    private void DhOnCmAccept()
    {
        if (!_services.Lua.Ready) { _services.Log.Warning("[DungeonHub] commission accept: Lua not ready"); return; }
        _services.Lua.DoString(DhCmWrap(DhCmAcceptLua));
        DhCmStarted("commission accept", keepOpen: true);
    }

    // The board's NPC: the game's own quick-jump for this activity (map trace to the board). The hub hides so the
    // game's map / tracking is visible.
    private void DhOnCmBoard()
    {
        if (!_services.Lua.Ready) return;
        _services.Lua.DoString(DhOpenWrap(DhCmBoardLua));
        _services.Log.Info($"[DungeonHub] commission go to the board: {_services.Lua.ReadGlobalString("__stellarDHOpen") ?? "(no result)"}");
        DhHide();
    }

    private void DhCmTeleport(int eventId, string what)
    {
        if (!_services.Lua.Ready) { _services.Log.Warning($"[DungeonHub] {what}: Lua not ready"); return; }
        _services.Lua.DoString(DhCmWrap(" local EID = " + Q(eventId) + DhCmTeleportLua));
        DhCmStarted(what, keepOpen: false);
    }

    private void DhCmStarted(string what, bool keepOpen)
    {
        var res = _services.Lua.ReadGlobalString("__stellarDHCm") ?? "ERR (no result)";
        _services.Log.Info($"[DungeonHub] {what}: {res}");
        if (res.StartsWith("SENT", StringComparison.Ordinal))
        {
            _dhCmWhat = what; _dhCmSince = DcNow; _dhCmWait = true; _dhFullDue = true;
            if (!keepOpen) DhHide();
        }
        else if (res.StartsWith("NO", StringComparison.Ordinal)) DhSetNotice($"Bureau Commissions: {res.Substring(2).Trim()}");
    }

    // Hub tick (also while hidden): the Accept / Teleport coroutine's result, or a timeout.
    private void DhCmFollow()
    {
        if (!_dhCmWait || !_services.Lua.Ready) return;
        var ret = _services.Lua.ReadGlobalString("__stellarDHCmRet");
        if (ret != null) { _dhCmWait = false; _services.Log.Info($"[DungeonHub] {_dhCmWhat}: result {ret}"); return; }
        if (DcNow - _dhCmSince < DhEnterWaitSec) return;
        _dhCmWait = false;
        _services.Log.Warning($"[DungeonHub] {_dhCmWhat}: no reply within {DhEnterWaitSec:0} s (an RPC error in the coroutine lands in the game log)");
    }

    private void DhOnCmEnter(int i)
    {
        if (DhCmEv(i) is not { } ev || !_services.Lua.Ready) return;
        string what = $"commission enter event={ev.Id} dungeon={ev.DungeonId} ({ev.Name})";
        _services.Lua.DoString(DhEnterWrap(" local EID = " + Q(ev.Id) + DhCmEnterLua));
        var res = _services.Lua.ReadGlobalString("__stellarDHEnter") ?? "ERR (no result)";
        if (res.StartsWith("SENT", StringComparison.Ordinal))
        {
            _services.Log.Info($"[DungeonHub] {what}: sent AsyncCreateLevel({res.Substring(4).Trim()})");
            _dhEnterWhat = what; _dhEnterSince = DcNow; _dhEnterWait = true; _dhEnterGh = false; _dhEnterCm = true;
            _dhCmEnterEvent = ev.Id; _dhNotice = "";
            DhHide();
            DhFollowEnter();   // a refusal before the RPC (Condition) returns synchronously
        }
        else if (res.StartsWith("NO", StringComparison.Ordinal))
        {
            _services.Log.Info($"[DungeonHub] {what}: not sent — {res.Substring(2).Trim()}");
            DhSetNotice($"Bureau Commissions: {res.Substring(2).Trim()}");
        }
        else
        {
            _services.Log.Warning($"[DungeonHub] {what}: {res} — fallback → teleport");
            DhCmTeleport(ev.Id, $"{what} fallback → teleport");
        }
    }

    // Enter.cs DhFollowEnter, for a commission enter: 0 = ok, nil = the entry Condition refused (tip shown), anything
    // else = a server code (e.g. it wants you at the event's spot) → the game's own Teleport to that event.
    private void DhCmAfterResult(string ret)
    {
        _dhEnterCm = false;
        if (ret == "0" || ret == "nil") return;
        _services.Log.Warning($"[DungeonHub] {_dhEnterWhat} (ret={ret}): fallback → teleport");
        DhCmTeleport(_dhCmEnterEvent, $"{_dhEnterWhat} fallback → teleport");
    }

    // ── Lua ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // Parks __stellarDHCm = 'SENT …' / 'NO <reason>' / 'ERR …'; the coroutine parks __stellarDHCmRet later.
    private static string DhCmWrap(string body) =>
        "pcall(function() rawset(_G, '__stellarDHCm', nil) rawset(_G, '__stellarDHCmRet', nil)" +
        " local m = Z.UIMgr local st = m.__stellarDH if not st then st = {} m.__stellarDH = st end local res = 'ERR no result'" +
        " local ok, err = pcall(function()" + body + " end)" +
        " rawset(_G, '__stellarDHCm', ok and res or ('ERR ' .. tostring(err))) end)";

    // The board's guards, then AsyncAcceptQuest (its own default token) + the success tip.
    private const string DhCmAcceptLua =
        " local wq = Z.VMMgr.GetVM('worldquest')" +
        " if wq.CheckHasWorldQuest() then res = 'NO the daily quest is already accepted' return end" +
        " if (Z.ContainerMgr.CharSerialize.worldEventMap.acceptCount or 0) == 0 then res = 'NO no accepts left' return end" +
        " local qid = Z.Global.WorldEventQuestId" +
        " Z.CoroUtil.create_coro_xpcall(function()" +
        "   local okA = Z.VMMgr.GetVM('quest').AsyncAcceptQuest(qid)" +
        "   if okA then Z.TipsVM.ShowTips(1381016) end" +
        "   rawset(_G, '__stellarDHCmRet', 'AsyncAcceptQuest=' .. tostring(okA))" +
        " end)()" +
        " res = 'SENT AsyncAcceptQuest(' .. tostring(qid) .. ')'";

    // WorldQuestTransfer is async (an RPC): our own rented CancelSource, as the World Boss fetch.
    private const string DhCmTeleportLua =
        " if not st.cmCs then st.cmCs = Z.CancelSource.Rent() end" +
        " local tok = st.cmCs:CreateToken()" +
        " Z.CoroUtil.create_coro_xpcall(function()" +
        "   local okT = Z.VMMgr.GetVM('worldquest').WorldQuestTransfer(EID, tok)" +
        "   if okT then Z.UIMgr:GotoMainView() end" +
        "   rawset(_G, '__stellarDHCmRet', 'WorldQuestTransfer=' .. tostring(okT))" +
        " end)()" +
        " res = 'SENT WorldQuestTransfer(' .. tostring(EID) .. ')'";

    private const string DhCmEnterLua =
        " local er = Z.TableMgr.GetTable('DailyWorldEventTableMgr').GetRow(EID, true)" +
        " if not er then res = 'PAGE no DailyWorldEventTable row ' .. tostring(EID) return end" +
        " local row = Z.TableMgr.GetTable('DungeonsTableMgr').GetRow(er.DungeonId, true)" +
        " if not row then res = 'PAGE no DungeonsTable row ' .. tostring(er.DungeonId) return end" +
        " send(row.FunctionID or E.FunctionID.WorldEvent, er.DungeonId, nil, nil, nil, nil)";

    // The recommended-play row's quick jump (this season's), else any season's SeasonActTable row for the function.
    private const string DhCmBoardLua =
        " local WE = E.FunctionID.WorldEvent" +
        " local cfg, src = nil, 'recommendedplay'" +
        " pcall(function() cfg = Z.DataMgr.Get('recommendedplay_data'):GetRecommendedPlayConfigByFunctionId(WE) end)" +
        " if not cfg or (cfg.QuickJumpType or 0) == 0 then cfg = nil src = 'SeasonActTable'" +
        "   for _, r in pairs(Z.TableMgr.GetTable('SeasonActTableMgr').GetDatas()) do" +
        "     if r.FunctionId == WE and (r.QuickJumpType or 0) > 0 and (not cfg or (r.SeasonId or 0) > (cfg.SeasonId or 0)) then cfg = r end" +
        "   end end" +
        " if not cfg then error('no quick-jump row for Bureau Commissions') end" +
        " local p = cfg.QuickJumpParam local ps = {} pcall(function() for i = 1, 3 do if p[i] then ps[#ps + 1] = tostring(p[i]) end end end)" +
        " Z.VMMgr.GetVM('quick_jump').DoJumpByConfigParam(cfg.QuickJumpType, p, { DynamicFlagName = cfg.Name, isShowRedInfo = false })" +
        " res = 'quick_jump ' .. src .. ' row=' .. tostring(cfg.Id) .. ' type=' .. tostring(cfg.QuickJumpType) .. ' param={' .. table.concat(ps, ',') .. '}'";

    // Poll (spliced after the G record; sees st, rec, b, try, TM). ⚠️ C# @"…" verbatim: no double quote in the Lua.
    private const string DhCmPollLua = @"
    pcall(function()
      local G = Z.Global
      local have = try(function() return Z.VMMgr.GetVM('worldquest').CheckHasWorldQuest() and true or false end, false)
      local wem = try(function() return Z.ContainerMgr.CharSerialize.worldEventMap end, nil)
      local ac = wem and try(function() return wem.acceptCount end, 0) or 0
      -- reset: timer 15 (WorldEventDailyRefreshTimer), re-read every 30 s; kept as an absolute second on st
      local nowS = math.floor(Z.TimeTools.Now() / 1000)
      if not st.cmResetRead or nowS - st.cmResetRead >= 30 or (st.cmResetAt or 0) <= nowS then
        st.cmResetRead = nowS st.cmResetAt = 0 st.cmResetHow = ''
        local tid = G.WorldEventDailyRefreshTimer or 15
        pcall(function() local v = tonumber(Z.TimeTools.GetNextRefreshRemainingTime(tid)) if v and v > 0 then st.cmResetAt = nowS + v st.cmResetHow = 'next' end end)
        if st.cmResetAt == 0 then pcall(function()
          local l, bf = Z.TimeTools.GetLeftTimeByTimerId(tid)
          l = tonumber(l) or -1 bf = tonumber(bf) or 0
          if bf > 0 then st.cmResetAt = nowS + bf st.cmResetHow = 'start' elseif l > 0 then st.cmResetAt = nowS + l st.cmResetHow = 'end' end
        end) end
      end
      local rs = (st.cmResetAt or 0) > 0 and (st.cmResetAt - nowS) or -1
      -- today's events, sorted by id like the board (worldquest_interactive_window_view.lua:114-124)
      local evs = {}
      if wem then pcall(function() for _, v in pairs(wem.eventMap) do evs[#evs + 1] = v end end) end
      table.sort(evs, function(a, c) return a.id < c.id end)
      local ET = TM.GetTable('DailyWorldEventTableMgr')
      local ST = TM.GetTable('SceneTableMgr')
      local f = { 'C', b(have), ac, G.WorldEventDailyCount or 3, G.WorldEventDaysLimit or 3, rs, st.cmResetHow or '', #evs }
      for _, v in ipairs(evs) do
        local r = try(function() return ET.GetRow(v.id, true) end, nil)
        local sc = r and r.Scene or 0
        local area = sc > 0 and try(function() return ST.GetRow(sc, true).Name end, '') or ''
        f[#f + 1] = v.id f[#f + 1] = r and r.Name or tostring(v.id) f[#f + 1] = area
        f[#f + 1] = b(v.award == 1) f[#f + 1] = r and r.DungeonId or 0
      end
      rec(table.unpack(f))
    end)
";
}
