using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — the World Boss tab (World Boss Crusade, FunctionID 800902). Game facts: Dungeon-Access-Hub.md §8
// "World Boss". Paths below are under Resources\release_3.7\lua\lua.
//   status  the event timer, NOT the function switch (open all week) and NOT SeasonActTable.OpenTimerId 601:
//           left, before = Z.TimeTools.GetLeftTimeByTimerId(Z.WorldBoss.WorldBossOpenTimerId = 220); open = left > 0 and
//           before <= 0; closed with before > 0 = a countdown; left <= 0 = ended (world_boss_main_view.lua RefreshRightInfo
//           :222-275). "Opens" = GetCycleTimeListByTimeId(220) as '%s %02d:%02d' with Lang(Z.Global.WeekText[wday]).
//   boss    this week's boss comes from the SERVER: world_boss:AsyncGetWorldBossInfo (world_boss_vm.lua:24-36) parks the
//           reply in world_boss_data; ret.bossCfgId → WorldBossSwitchTable (RecommendMainPic card art, MainPic, DungeonsId
//           → DungeonsTable.Name). ⚠️ match_activity_vm CheckCanMatchTeam (:110-130) indexes
//           GetWorldBossInfoData().bossCfgId for a party → nil-index error if it was never fetched, so Match fetches first.
//   match   the page's btnGo (world_boss_main_view.lua:80-106): leader tip 16002048, the "no rewards left" preferences
//           dialog on counter 21, then RequestBeginMatch(Activity, recommendedplay SeasonActTable id).
// W record (every poll, after T): open, left, before, opensText, bossName, art, bossCfgId, haveInfo, log.
public sealed partial class Plugin
{
    private const int DhTabWb = 2, DhWbFunc = 800902;

    private sealed class DhWbState
    {
        public bool Open, HaveInfo;
        public int Left, Before, CfgId;
        public double At;                      // DcNow of the poll (the countdown ticks locally from there)
        public string Opens = "", Boss = "", Art = "", Log = "";
    }

    private DhWbState? _dhWb;
    private int _dhWbLoggedCfg = -1;
    private string _dhWbLoggedLog = "";

    private static bool DhIsWb(DhEntry? e) => e is { IsActivity: true, FuncId: DhWbFunc };

    private void DhParseWb(string[] f)
    {
        var w = new DhWbState
        {
            Open = f[1] == "1", Left = DcInt(f[2]), Before = DcInt(f[3]), Opens = f[4], Boss = f[5], Art = f[6],
            CfgId = DcInt(f[7]), HaveInfo = f[8] == "1", Log = f[9], At = DcNow,
        };
        _dhWb = w;
        if (w.CfgId != _dhWbLoggedCfg && w.HaveInfo)
        {
            _dhWbLoggedCfg = w.CfgId;
            _services.Log.Info($"[DungeonHub] world boss: this week cfg={w.CfgId} ({w.Boss}) art={w.Art} open={w.Open} left={w.Left} before={w.Before} opens='{w.Opens}'");
        }
        if (w.Log.Length > 0 && w.Log != _dhWbLoggedLog) { _dhWbLoggedLog = w.Log; _services.Log.Info($"[DungeonHub] world boss: {w.Log}"); }
    }

    // The thumbnail / band art: this week's boss card (RecommendMainPic) once the boss info is in, else the catalog's.
    // Guild Hunt: its UnionActivityTable BackGroundPic (.GuildHunt.cs G record).
    private string DhArtOf(DhEntry e) => DhIsWb(e) && _dhWb is { Art.Length: > 0 } w ? w.Art
        : DhIsGh(e) && _dhGh is { Art.Length: > 0 } g ? g.Art : e.Art;

    // ── countdown "1d 16h 25m" / "16h 25m" / "25m 03s" / "45s", ticked locally from the last poll ─────────────────
    private string _dhWbCd = ""; private int _dhWbCdSec = -1;
    private string DhWbCountdown()
    {
        var w = _dhWb;
        if (w == null) return "";
        int s = Math.Max(0, (int)(w.Before - Math.Max(0, DcNow - w.At)));
        if (s == _dhWbCdSec) return _dhWbCd;
        _dhWbCdSec = s;
        int d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60, sec = s % 60;
        return _dhWbCd = d > 0 ? $"{d}d {h}h {m:00}m" : h > 0 ? $"{h}h {m:00}m" : m > 0 ? $"{m}m {sec:00}s" : $"{sec}s";
    }

    // Open · Closed (a next start is known) · Ended (left <= 0, the page's ActivityHasEnd) · Closed (neither).
    private string DhWbStatus() => _dhWb switch
    {
        null => "—",
        { Open: true } => "Open",
        { Before: > 0 } => "Closed",
        { Left: <= 0 } => "Ended",
        _ => "Closed",
    };

    private ColorRgba DhWbStatusColor() => _dhWb switch
    {
        null => MutedColor(),
        { Open: true } => DhGreen,
        { Before: <= 0, Left: <= 0 } => MutedColor(),
        _ => DhAmber,
    };

    // The list sub-line and the Match button's reason line while not open.
    private string DhWbRowSub(DhEntry e)
    {
        var w = _dhWb;
        if (w == null) return e.SubText;
        if (w.Open) return e.SubText.Length > 0 ? $"Open · {e.SubText}" : "Open";
        if (w.Before > 0) return $"Closed · starts in {DhWbCountdown()}";
        return w.Left <= 0 ? "Ended" : "Closed";
    }

    private string DhWbClosedSub()
    {
        var w = _dhWb;
        if (w == null) return "closed";
        if (w.Before > 0) return $"starts in {DhWbCountdown()}";
        return w.Left <= 0 ? "ended" : "closed";
    }

    private string DhWbClosedNotice() => _dhWb is { Opens.Length: > 0 } w
        ? $"World Boss is closed · opens {w.Opens}" : "World Boss is closed";

    // ── detail panel bits ─────────────────────────────────────────────────────────────────────────────────────────
    // "This week: <boss>" under the title (empty on the other activity tabs — same height, so nothing moves).
    private string DhActSubLine()
    {
        var e = DhCur;
        return DhIsWb(e) && _dhWb is { Boss.Length: > 0 } w ? $"This week: {w.Boss}" : "";
    }

    // Rewards left · Status (the event timer) · Opens (the game's own open-time text).
    private HudElement DhWbTiles() => new RowElement(new HudElement[]
    {
        DhTile(() => DhCur?.CounterKey is { Length: > 0 } k ? k : "Rewards left",
               () => DhCur?.TileVal is { Length: > 0 } v ? v : "—",
               () => DhCur is { CounterZero: true } ? MutedColor() : TextColor(), DhThirdW),
        DhTile(() => "Status", () => DhCur is { Locked: true } ? "Locked" : DhWbStatus(),
               () => DhCur is { Locked: true } ? MutedColor() : DhWbStatusColor(), DhThirdMidW),
        DhTile(() => "Opens", () => _dhWb is { Opens.Length: > 0 } w ? w.Opens : "—", () => TextColor(), DhThirdW),
    }, Gap: DhPanGap);

    // ── actions ───────────────────────────────────────────────────────────────────────────────────────────────────
    // Hub open / World Boss tab shown: fetch this week's boss (throttled to one RPC per 10 s on the Lua side).
    private void DhWbFetch()
    {
        if (_services.ClientState.Phase != GamePhase.World || !_services.Lua.Ready) return;
        _services.Lua.DoString(DhWbFetchChunk);
    }

    private void DhOnWbMatch(DhEntry e)
    {
        if (!_services.Lua.Ready) { _services.Log.Warning("[DungeonHub] world boss match: Lua not ready"); return; }
        string what = $"match world boss act={e.MatchActId} ({e.Name})";
        if (_dhWb is { Open: false })
        {
            _services.Log.Info($"[DungeonHub] {what}: not sent — closed (left={_dhWb.Left} before={_dhWb.Before})");
            DhSetNotice(DhWbClosedNotice());
            return;
        }
        _services.Lua.DoString(DhWbMatchChunk(e.MatchActId));
        var res = _services.Lua.ReadGlobalString("__stellarDHOpen") ?? "(no result)";
        _services.Log.Info($"[DungeonHub] {what}: {res}");
        if (res == "NO closed") DhSetNotice(DhWbClosedNotice());
        else if (res == "NO leader") DhSetNotice("World Boss: only the party leader can start the match");
        _dhFullDue = true;
    }

    // ── Lua ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // Spliced into the poll after the T record (sees its locals: st, rec, b, try, TM, DT). ⚠️ C# @"…" verbatim string:
    // no double quote anywhere in the Lua, comments included. The open-time text is cached on st (a weekly cycle).
    private const string DhWbPollLua = @"
    pcall(function()
      local WB = Z.WorldBoss
      local tid = WB.WorldBossOpenTimerId
      local left, before = Z.TimeTools.GetLeftTimeByTimerId(tid)
      left = math.floor(tonumber(left) or -1) before = math.floor(tonumber(before) or 0)
      if not st.wbOpens or st.wbOpens == '' then
        local okT, s = pcall(function()
          local starts = Z.TimeTools.GetCycleTimeListByTimeId(tid)
          local parts = {}
          for _, v in ipairs(starts or {}) do
            local wd = nil
            pcall(function() wd = Lang(Z.Global.WeekText[v.wday]) end)
            if not wd or wd == '' then wd = ({ 'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat' })[v.wday] or '?' end
            parts[#parts + 1] = string.format('%s %02d:%02d', wd, v.hour, v.min)
          end
          return table.concat(parts, ', ')
        end)
        st.wbOpens = okT and s or ''
      end
      local function nz(s) if s and s ~= '' then return s end return nil end
      local name, art, cfgId = '', '', 0
      local info = try(function() return Z.DataMgr.Get('world_boss_data'):GetWorldBossInfoData() end, nil)
      if info then
        cfgId = info.bossCfgId or 0
        local row = TM.GetTable('WorldBossSwitchTableMgr').GetRow(cfgId, true)
        if row then
          art = nz(row.RecommendMainPic) or nz(row.MainPic) or ''
          local d = DT.GetRow(row.DungeonsId, true)
          name = d and d.Name or ''
        end
      end
      rec('W', b(left > 0 and before <= 0), left, before, st.wbOpens, name, art, cfgId, b(info ~= nil), st.wbLog or '')
    end)
";

    // The page's own fetch (world_boss_main_view.lua OnActive :26-32), in a coroutine (RPC) with our own CancelSource.
    // The reply lands in world_boss_data, which the poll's W record reads (so a fetch by the game's page counts too).
    private const string DhWbFetchChunk =
        "pcall(function() local m = Z.UIMgr if not m then return end" +
        " local st = m.__stellarDH if not st then st = {} m.__stellarDH = st end" +
        " local now = Z.TimeTools.Now()" +
        " if st.wbFetchAt and now - st.wbFetchAt < 10000 then return end" +
        " st.wbFetchAt = now" +
        " if not st.wbCs then st.wbCs = Z.CancelSource.Rent() end" +
        " Z.CoroUtil.create_coro_xpcall(function()" +
        "   Z.VMMgr.GetVM('world_boss'):AsyncGetWorldBossInfo(st.wbCs:CreateToken(), function(ret)" +
        "     st.wbLog = 'boss info fetched, cfg=' .. tostring(ret and ret.bossCfgId) end)" +
        " end)() end)";

    // Match, in the order the user approved: not open → NO closed; party member → tip 16002048, NO leader; no boss info
    // yet → fetch it first (the nil-index trap above), then continue in that coroutine; counter 21 at 0 → the game's
    // WorldBossLimitOver preferences dialog (its OK runs go); else go. go = the page's func: the recommended-play row's
    // Id for FunctionID WorldBoss (ACT, the catalog's copy, only as a fallback).
    private static string DhWbMatchChunk(int actId) => DhOpenWrap(
        " local WB = Z.WorldBoss" +
        " local left, before = Z.TimeTools.GetLeftTimeByTimerId(WB.WorldBossOpenTimerId)" +
        " if not ((tonumber(left) or -1) > 0 and (tonumber(before) or 1) <= 0) then res = 'NO closed' return end" +
        " local tv = Z.VMMgr.GetVM('team')" +
        " if tv.CheckIsInTeam() and not tv.GetYouIsLeader() then Z.TipsVM.ShowTips(16002048) res = 'NO leader' return end" +
        " local function go()" +
        "   local cfg = Z.DataMgr.Get('recommendedplay_data'):GetRecommendedPlayConfigByFunctionId(E.FunctionID.WorldBoss)" +
        "   local id = cfg and cfg.Id or " + Q(actId) +
        "   Z.VMMgr.GetVM('match').RequestBeginMatch(E.MatchType.Activity, id, Z.DataMgr.Get('match_data').CancelSource:CreateToken())" +
        "   return 'RequestBeginMatch(Activity, ' .. tostring(id) .. ')'" +
        " end" +
        " local function step()" +
        "   local cid = WB.WorldBossAwardCountId" +
        "   local lim = Z.CounterHelper.GetCounterLimitCount(cid)" +
        "   if Z.CounterHelper.GetCounterResidueLimitCount(cid, lim) == 0 then" +
        "     Z.DialogViewDataMgr:CheckAndOpenPreferencesDialog(Lang('WorldBossLimitOver'), go, nil, E.DlgPreferencesType.Login, E.DlgPreferencesKeyType.WorldBossMatch)" +
        "     return 'limit dialog'" +
        "   end" +
        "   return go()" +
        " end" +
        " local wd = Z.DataMgr.Get('world_boss_data')" +
        " if wd:GetWorldBossInfoData() == nil then" +
        "   if not st.wbCs then st.wbCs = Z.CancelSource.Rent() end" +
        "   Z.CoroUtil.create_coro_xpcall(function()" +
        "     Z.VMMgr.GetVM('world_boss'):AsyncGetWorldBossInfo(st.wbCs:CreateToken())" +
        "     if wd:GetWorldBossInfoData() == nil then st.wbLog = 'match: boss info fetch failed, not sent' return end" +
        "     local okS, r = pcall(step)" +
        "     st.wbLog = 'match after boss-info fetch: ' .. (okS and tostring(r) or ('ERR ' .. tostring(r)))" +
        "   end)()" +
        "   res = 'FETCH boss info first, then match'" +
        "   return" +
        " end" +
        " res = step()");
}
