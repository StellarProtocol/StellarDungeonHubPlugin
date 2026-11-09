using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — Guild Hunt (Hunt tab, FunctionID 500151), entered directly like a dungeon. Game facts:
// Dungeon-Access-Hub.md §8 "Guild Hunt". Paths below are under Resources\release_3.7\lua\lua.
//   why     GoToFunc(500151) lands on the guild window's Hunt TAB, not the enter page — so the old "Open" went to the
//           wrong place. The enter page (union_hunt_enter_into_main) is opened from the hunt NPC in the guild scene.
//   enter   the page's Go (union_hunt_enter_into_main_view.lua:141-200) → union:AsyncStartEnterDungeon(FunctionID, id,
//           hero_dungeon_main.GetAffix(id), cs) = ui_enterdungeonscene AsyncCreateLevel(fid, id, token, affix, nil, nil,
//           nil) with ret DROPPED (union_vm.lua:1698-1701) → we call AsyncCreateLevel ourselves (Enter.cs DhEnterWrap)
//           to log the code. No matchmaking (no TeamTargetTable row); Solo allowed (LimitedNum [1,5] → MinCount 1).
//   ids     Z.UnionActivityConfig.HuntActivityId entries with [2] == E.UnionActivityType.UnionHunt, in order: 1 Normal
//           (12022 in season 3), 2 Hard (12023) — read live, as the page's initBaseData does.
//   checks  in guild (GetPlayerUnionId ~= 0) · FuncIsOn(E.UnionFuncId.Hunt) · CheckUnionHuntUnlock (union_vm.lua:402-413,
//           UnionActivityTable[1].TimerId 251) · team members outside the guild (team:GetNotMyUnionMemberInTeam, tip
//           1004004) · rewards: counter 4 at 0 → tip 1004012, else the difficulty's counter (GetDungeonCounterID: 12022 →
//           5, 12023 → 6) at 0 → tip 1004011 — both still send, like the page.
//   status  open = CheckUnionHuntUnlock(); "starts in" from Z.TimeTools.GetLeftTimeByTimerId(251) 'before', ticked locally.
//   counter the page's own raw read: CounterTable.Limit - CharSerialize.counterList.counterMap[id].counter.
//   fallback unexpected chunk data, or a non-zero server code → union:EnterUnionSceneHunt() (walks / teleports to the hunt
//           NPC in the guild scene; refuses with tips 1000593/1000594/100124/1000555, union_vm.lua:1555-1592). Logged.
// G record (every poll, after W): inGuild, open, left, before, actRem, actLim, art, (id, rec, rem, lim) × 2,
//   recIbs × 2 (Normal, Hard: the row's SeasonLv[3] via the poll's ibsOf, 0 = none → the tile shows the AS alone).
public sealed partial class Plugin
{
    private const int DhGhFunc = 500151;
    private static readonly string[] DhGhDiffs = { "Normal", "Hard" };          // log names; the chips show DhGhDiffKeys
    private static readonly string[] DhGhDiffKeys = { "dh.diff.normal", "dh.diff.hard" };

    private sealed class DhGhState
    {
        public bool InGuild, Open;
        public int Left, Before, ActRem, ActLim;
        public double At;                      // DcNow of the poll (the countdown ticks locally from there)
        public string Art = "";
        public readonly int[] Ids = new int[2], Rec = new int[2], Rem = new int[2], Lim = new int[2], Ibs = new int[2];
    }

    private DhGhState? _dhGh;
    private int _dhGhDiff;                     // 0 Normal · 1 Hard
    private bool _dhEnterGh;                   // the enter being followed (Enter.cs DhFollowEnter) is a Guild Hunt one
    private string _dhGhLogged = "";

    private static bool DhIsGh(DhEntry? e) => e is { IsActivity: true, FuncId: DhGhFunc };

    private void DhParseGh(string[] f)
    {
        var g = new DhGhState
        {
            InGuild = f[1] == "1", Open = f[2] == "1", Left = DcInt(f[3]), Before = DcInt(f[4]),
            ActRem = DcInt(f[5]), ActLim = DcInt(f[6]), Art = f[7], At = DcNow,
        };
        for (int i = 0; i < 2; i++)
        {
            g.Ids[i] = DcInt(f[8 + 4 * i]); g.Rec[i] = DcInt(f[9 + 4 * i]);
            g.Rem[i] = DcInt(f[10 + 4 * i]); g.Lim[i] = DcInt(f[11 + 4 * i]);
            if (f.Length > 16 + i) g.Ibs[i] = DcInt(f[16 + i]);
        }
        _dhGh = g;
        string sig = $"guild={g.InGuild} open={g.Open} ids={g.Ids[0]},{g.Ids[1]} rewards={g.ActRem}/{g.ActLim} " +
                     $"diff={g.Rem[0]}/{g.Lim[0]},{g.Rem[1]}/{g.Lim[1]}";
        if (sig != _dhGhLogged)
        {
            _dhGhLogged = sig;
            _services.Log.Info($"[DungeonHub] guild hunt: {sig} left={g.Left} before={g.Before} art={g.Art}");
        }
    }

    // ── countdown "1d 16h 25m" / "16h 25m" / "25m 03s" / "45s", ticked locally from the last poll (as World Boss) ───
    private string _dhGhCd = ""; private int _dhGhCdSec = -1;
    private string DhGhCountdown()
    {
        var g = _dhGh;
        if (g == null) return "";
        int s = Math.Max(0, (int)(g.Before - Math.Max(0, DcNow - g.At)));
        if (s == _dhGhCdSec) return _dhGhCd;
        _dhGhCdSec = s;
        return _dhGhCd = DhDur(s);   // Plugin.Loc.cs
    }

    private bool DhGhClosed => _dhGh is { Open: false };
    private string DhGhStartsIn() => _dhGh is { Open: false, Before: > 0 } ? LF("dh.sub.startsIn", DhGhCountdown()) : "";
    private string DhGhClosedSub() => DhGhStartsIn() is { Length: > 0 } s ? s : L("dh.sub.closed");
    private string DhGhClosedNotice() => _dhGh is { Open: false, Before: > 0 } ? LF("dh.gh.closedIn", DhGhCountdown()) : L("dh.gh.closed");
    private string DhGhRewards() => _dhGh is { ActLim: > 0 } g ? $"{g.ActRem} / {g.ActLim}" : "";

    // List sub-line: Not in a guild · Open · Rewards left x / 2 · Closed · starts in … ("Locked" comes first, DhRowSub).
    private string DhGhRowSub(DhEntry e)
    {
        var g = _dhGh;
        if (g == null) return e.SubText;
        if (!g.InGuild) return L("dh.gh.notInGuild");
        if (g.Open) return DhGhRewards() is { Length: > 0 } r ? LF("dh.row.open", LF("dh.counter", r)) : L("dh.st.open");
        return g.Before > 0 ? LF("dh.row.closedIn", DhGhCountdown()) : L("dh.st.closed");
    }

    // Solo / Party send the same call (the game has no solo/team choice here: selectType nil); Solo = not in a party,
    // Party = in one, as its leader. Match: Guild Hunt has no matchmaking.
    private DhBtn DhGhBtnState(int which, string label)
    {
        var e = DhCur; var live = _dhLive; var g = _dhGh;
        if (which == 1) return new(false, false, label, L("dh.sub.notForGh"));
        if (e == null || g == null) return new(false, false, label, "");
        if (e.Locked) return new(false, false, label, L("dh.sub.locked"));
        if (!g.InGuild) return new(false, false, label, L("dh.sub.notInGuild"));
        if (!g.Open) return new(false, false, label, DhGhClosedSub());
        if (live.Matching) return new(false, false, label, "");
        if (which == 0) return live.InTeam ? new(false, false, label, L("dh.sub.youInParty")) : new(true, false, label, L("dh.sub.onYourOwn"));
        if (!live.InTeam) return new(false, false, label, L("dh.sub.notInParty"));
        return live.Leader ? new(true, false, label, L("dh.sub.withParty")) : new(false, false, label, L("dh.sub.leaderOnly"));
    }

    // ── detail panel: the dungeon panel's skeleton (art · name · DIFFICULTY · chips · mid block · Solo/Match/Party), so
    // the window height is the same as on the Dungeons tab. Tiles: Recommended · Status (+ "starts in") · Rewards left.
    private HudElement DhGhPanel() => DhPanelNoCaption(
        DhArtBand(),
        DhNameRow(),
        DhCaption(() => L("dh.cap.difficulty")),
        new RowElement(new HudElement[] { DhGhChip(0, DhChipW), DhGhChip(1, DhChipW) }, Gap: DhPanGap),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhMidH),
            new CellElement(DhTopPinned(DhGhTiles()), Width: DhDetInW),
        }, Gap: 0.01f),
        DhCaption(() => L("dh.cap.howToGo")),   // outside the block: always directly above the buttons (Detail.cs)
        new RowElement(new HudElement[]
        {
            DhGoButton(0, DhThirdW, DhOnSolo),
            DhGoButton(1, DhThirdMidW, DhOnMatch),
            DhGoButton(2, DhThirdW, DhOnParty),
        }, Gap: DhPanGap));

    // Taller than DhTile (the Status tile carries a third "starts in" line while closed); the mid block's strut has
    // the room. The two Status looks are WHOLE bubbles swapped by a Conditional (never a Conditional inside a bubble).
    private static readonly float DhGhTileH = DcSE(52f);   // 83

    private HudElement DhGhTiles() => new RowElement(new HudElement[]
    {
        DhGhTile(() => L("dh.tile.recommended"), () => _dhGh is { } g && g.Rec[_dhGhDiff] > 100 ? DhAsIbs(_dhGhRecTxt, g.Rec[_dhGhDiff], g.Ibs[_dhGhDiff]) : "—",
            () => TextColor(), null, DhThirdW),
        new ConditionalElement(() => DhGhStartsIn().Length > 0 && DhCur is { Locked: false },
            DhGhTile(() => L("dh.tile.status"), () => L("dh.st.closed"), () => DhAmber, DhGhStartsIn, DhThirdMidW),
            DhGhTile(() => L("dh.tile.status"), DhGhStatus, DhGhStatusColor, null, DhThirdMidW)),
        DhGhTile(() => L("dh.word.rewardsLeft"),
            () => DhGhRewards() is { Length: > 0 } r ? (_dhGh!.ActRem <= 0 ? LF("dh.counter.none", r) : r) : "—",
            () => _dhGh is { ActLim: > 0, ActRem: <= 0 } ? MutedColor() : TextColor(), null, DhThirdW),
    }, Gap: DhPanGap);

    private string DhGhStatus() => DhCur is { Locked: true } ? L("dh.word.locked") : _dhGh switch
    {
        null => "—",
        { InGuild: false } => L("dh.gh.notInGuild"),
        { Open: true } => L("dh.st.open"),
        _ => L("dh.st.closed"),
    };

    private ColorRgba DhGhStatusColor() => DhCur is { Locked: true } ? MutedColor() : _dhGh switch
    {
        null or { InGuild: false } => MutedColor(),
        { Open: true } => DhGreen,
        _ => DhAmber,
    };

    private HudElement DhGhTile(Func<string> label, Func<string> value, Func<ColorRgba> valueColor, Func<string>? sub, float w)
    {
        var lines = new System.Collections.Generic.List<HudElement>
        {
            new TextElement(label, Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub) { RichEmoji = true, Ellipsize = true },
            // RichEmoji: Recommended's "AS (IBS)" carries an inline <color> tag (Detail.cs DhAsIbs)
            new TextElement(value, Color: () => valueColor(), NoWrap: true, FontSize: DhFsBody) { RichEmoji = true, Bold = true, Ellipsize = true },
        };
        if (sub != null)
            lines.Add(new TextElement(sub, Color: () => MutedColor(), NoWrap: true, FontSize: DhFsTiny) { RichEmoji = true, Ellipsize = true });
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(DcSE(8f), DhGhTileH),
            new CellElement(new ColumnElement(lines.ToArray(), Gap: DcS(1f)), Width: w - 2f * DcSE(8f)),
            new SpacerElement(DcSE(8f), 0f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(DcTile()), CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
        }, Width: w);
    }

    // Normal (green) · Hard (amber), the dungeon chips' look. Both always drawn (a missing id = dimmed, not clickable)
    // so the row never changes height before the first poll.
    private HudElement DhGhChip(int i, float w)
    {
        var hover = new DcHover();
        ColorRgba kind = i == 0 ? DhGreen : DhAmber;
        Func<bool> has = () => _dhGh is { } g && g.Ids[i] > 0;
        Func<bool> on = () => _dhGhDiff == i && has();
        Func<ColorRgba> fill = () => on() ? DcWash(kind, 0.25f)
                                    : hover.On ? Over(Menu.MenuText with { A = 0.08f }, DcNeutral()) : DcNeutral();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DhChipH),
                DcDrop(DcBold(() => L(DhGhDiffKeys[i]),
                    () => on() ? EnsureContrast(kind, fill(), 4.5f) : has() ? MutedColor() : MutedColor() with { A = 0.5f },
                    DhFsBody, w, TextAlign.Center)),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: () => { if (has()) _dhGhDiff = i; }) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(fill()),
                OutlineColorFn = () => FadeRing(on() ? kind with { A = 0.6f } : null),
                CornerRadius = DhChipH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            }, Width: w);
    }

    // ── enter ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void DhOnGhEnter(bool solo)
    {
        var e = DhCur; var g = _dhGh;
        if (e == null || !DhBtnState(solo ? 0 : 2).Enabled) return;
        if (!_services.Lua.Ready) { _services.Log.Warning("[DungeonHub] guild hunt enter: Lua not ready"); return; }
        int diff = Math.Clamp(_dhGhDiff, 0, 1);
        string what = $"guild hunt enter diff={DhGhDiffs[diff]} id={g?.Ids[diff] ?? 0} sel={(solo ? "solo" : "party")}";
        if (g is { Open: false })
        {
            _services.Log.Info($"[DungeonHub] {what}: not sent — closed (left={g.Left} before={g.Before})");
            DhSetNotice(DhGhClosedNotice());
            return;
        }
        _services.Lua.DoString(DhGhEnterChunk(diff + 1, solo));
        var res = _services.Lua.ReadGlobalString("__stellarDHEnter") ?? "ERR (no result)";
        if (res.StartsWith("SENT", StringComparison.Ordinal))
        {
            string args = res.Substring(4).Trim(), warn = "";
            int w = args.IndexOf(" WARN ", StringComparison.Ordinal);
            if (w >= 0) { warn = args.Substring(w + 6).Trim(); args = args.Substring(0, w); }
            _services.Log.Info($"[DungeonHub] {what}: sent AsyncCreateLevel({args})" +
                (warn.Length > 0 ? $" — no {warn} rewards left (game tip shown, sent anyway)" : ""));
            _dhEnterWhat = what; _dhEnterSince = DcNow; _dhEnterWait = true; _dhEnterGh = true; _dhEnterCm = false;
            if (warn.Length > 0) DhSetNotice(L("dh.gh.noRewards")); else _dhNotice = "";
            DhHide();          // Party → the team_enter vote (our Dungeon Confirm, inferred), Solo → loading
            DhFollowEnter();   // a refusal before the RPC (Condition) returns synchronously
        }
        else if (res.StartsWith("NO", StringComparison.Ordinal))
        {
            string why = res.Substring(2).Trim();
            _services.Log.Info($"[DungeonHub] {what}: not sent — {why}");
            DhSetNotice(why == "closed" ? DhGhClosedNotice()
                : why == "leader only" ? L("dh.gh.leaderOnly") : DhReason(why));
        }
        else
        {
            _services.Log.Warning($"[DungeonHub] {what}: {res} — falling back to the hunt NPC");
            DhGhFallback(what);
            DhHide();
        }
    }

    // Enter.cs DhFollowEnter, for a Guild Hunt enter: 0 = ok, nil = the entry Condition refused (game tip shown, the NPC
    // would refuse too), anything else = a server code → the hidden fallback (e.g. the server wants the guild scene).
    private void DhGhAfterResult(string ret)
    {
        _dhEnterGh = false;
        if (ret != "0" && ret != "nil") DhGhFallback($"{_dhEnterWhat} (ret={ret})");
    }

    private void DhGhFallback(string what)
    {
        _services.Lua.DoString(DhOpenWrap(" Z.VMMgr.GetVM('union'):EnterUnionSceneHunt() res = 'EnterUnionSceneHunt()'"));
        _services.Log.Warning($"[DungeonHub] {what}: fallback → hunt NPC in the guild scene: {_services.Lua.ReadGlobalString("__stellarDHOpen") ?? "(no result)"}");
    }

    // The page's Go, its checks in the user-approved order (see the header). DIFF = 1 Normal / 2 Hard (the page's own
    // difficulty index into the HuntActivityId list). Reasons after 'NO' are shown as-is in the bottom bar.
    private static string DhGhEnterChunk(int diff, bool solo) => DhEnterWrap(
        " local SOLO, DIFF = " + Q(solo) + ", " + Q(diff) +
        " local uvm = Z.VMMgr.GetVM('union')" +
        " if (uvm:GetPlayerUnionId() or 0) == 0 then res = 'NO Not in a guild' return end" +
        " if not Z.VMMgr.GetVM('gotofunc').FuncIsOn(E.UnionFuncId.Hunt, true) then res = 'NO Guild Hunt is locked' return end" +
        " if not uvm:CheckUnionHuntUnlock() then res = 'NO closed' return end" +
        " local ids = {}" +
        " for _, v in ipairs(Z.UnionActivityConfig.HuntActivityId) do if v[2] == E.UnionActivityType.UnionHunt then ids[#ids + 1] = v[1] end end" +
        " local id = ids[DIFF]" +
        " if not id then res = 'PAGE no hunt dungeon for difficulty ' .. DIFF return end" +
        " local row = Z.TableMgr.GetTable('DungeonsTableMgr').GetRow(id, true)" +
        " if not row then res = 'PAGE no DungeonsTable row ' .. tostring(id) return end" +
        DhEnterTeamLua +
        " if SOLO and inTeam then res = 'NO You are in a party - use Party' return end" +
        " if not SOLO and not inTeam then res = 'NO Not in a party - use Solo' return end" +
        // the page's MinCount (DungeonPeopleCount → LimitedNum[1], 1 on season 3): above 1 it needs a party (tip 3322)
        " local minC = lim(row.LimitedNum, 1) or 1" +
        " if minC > 1 and not inTeam then Z.TipsVM.ShowTips(3322) res = 'NO Guild Hunt needs a party' return end" +
        " if inTeam then" +
        "   local names = {}" +
        "   for _, v in ipairs(tv:GetNotMyUnionMemberInTeam() or {}) do" +
        "     local n = '?' pcall(function() n = v.socialData.basicData.name end) names[#names + 1] = tostring(n)" +
        "   end" +
        "   if #names > 0 then res = 'NO Not in your guild: ' .. table.concat(names, ', ') return end" +
        " end" +
        // rewards: still sends, like the page (tip 1004012 = no guild-hunt rewards, else 1004011 = none on this difficulty)
        " local function residue(cid) local l, u = 0, 0" +
        "   pcall(function() l = Z.TableMgr.GetTable('CounterTableMgr').GetRow(cid, true).Limit or 0 end)" +
        "   pcall(function() local c = Z.ContainerMgr.CharSerialize.counterList.counterMap[cid] u = c and c.counter or 0 end)" +
        "   return l - u, l end" +
        " local warn = ''" +
        " local aRem, aLim = residue(Z.UnionActivityConfig.HuntUnionAwardCounter or 4)" +
        " if aLim > 0 and aRem <= 0 then pcall(function() Z.TipsVM.ShowTipsLang(1004012) end) warn = 'guild hunt'" +
        " else" +
        "   local dc = uvm:GetDungeonCounterID(id) or 0" +
        "   if dc > 0 then local dRem, dLim = residue(dc)" +
        "     if dLim > 0 and dRem <= 0 then pcall(function() Z.TipsVM.ShowTipsLang(1004011, { val = dLim }) end) warn = 'difficulty' end end" +
        " end" +
        " send(row.FunctionID, id, Z.VMMgr.GetVM('hero_dungeon_main').GetAffix(id), nil, nil, nil)" +
        " if warn ~= '' then res = res .. ' WARN ' .. warn end");

    // ── poll (spliced after the W record; sees st, rec, b, try, TM, DT). ⚠️ C# @"…" verbatim: no double quote in the Lua.
    private const string DhGhPollLua = @"
    pcall(function()
      local uvm = Z.VMMgr.GetVM('union')
      local UH = E.UnionActivityType.UnionHunt
      local inG = (try(function() return uvm:GetPlayerUnionId() end, 0) or 0) ~= 0
      local open = try(function() return uvm:CheckUnionHuntUnlock() and true or false end, false)
      local ar = try(function() return TM.GetTable('UnionActivityTableMgr').GetRow(UH, true) end, nil)
      local tid = ar and ar.TimerId or 251
      local left, before = -1, 0
      pcall(function() left, before = Z.TimeTools.GetLeftTimeByTimerId(tid) end)
      left = math.floor(tonumber(left) or -1) before = math.floor(tonumber(before) or 0)
      -- the page's own raw counter read (union_hunt_enter_into_main_view.lua:232-275)
      local function cnt(cid)
        local l = try(function() return TM.GetTable('CounterTableMgr').GetRow(cid, true).Limit end, 0) or 0
        local u = try(function() local c = Z.ContainerMgr.CharSerialize.counterList.counterMap[cid] return c and c.counter end, 0) or 0
        return math.max(0, l - u), l
      end
      local aRem, aLim = cnt(Z.UnionActivityConfig.HuntUnionAwardCounter or 4)
      -- art: this season's active hunt row (union:GetUnionHuntData), else the table row
      local art = ''
      pcall(function()
        for _, r in ipairs(uvm:GetUnionHuntData() or {}) do
          if r.Type == UH and (r.BackGroundPic or '') ~= '' then art = r.BackGroundPic break end
        end
      end)
      if art == '' and ar then art = ar.BackGroundPic or '' end
      local f = { 'G', b(inG), b(open), left, before, aRem, aLim, art }
      local ids = {}
      pcall(function()
        for _, v in ipairs(Z.UnionActivityConfig.HuntActivityId) do if v[2] == UH then ids[#ids + 1] = v[1] end end
      end)
      local ibs = { 0, 0 }
      for i = 1, 2 do
        local id = ids[i] or 0
        local row = id > 0 and DT.GetRow(id, true) or nil
        local rem, lim = 0, 0
        local cid = id > 0 and try(function() return uvm:GetDungeonCounterID(id) end, 0) or 0
        if cid > 0 then rem, lim = cnt(cid) end
        f[#f + 1] = id f[#f + 1] = row and row.RecommendFightValue or 0 f[#f + 1] = rem f[#f + 1] = lim
        ibs[i] = ibsOf(row)
      end
      -- recommended IBS per difficulty AFTER the four-field blocks, so the older layout's indices stay put
      f[#f + 1] = ibs[1] f[#f + 1] = ibs[2]
      rec(table.unpack(f))
    end)
";
}
