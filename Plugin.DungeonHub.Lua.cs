using System.Globalization;

namespace Stellar.DungeonHub;

// Dungeons hub — every Lua chunk. Game facts: Knowledge Base\Dungeon-Access-Hub.md (§8 = what this build found).
// Lua never calls C# (native Lua→C# callbacks crash this game): each chunk is a pcall'd DoString that parks its result
// in a _G string (rawset, strict-global guard) and C# reads it back with ReadGlobalString (Framework-Interop-
// Foundation.md §ILua park-and-read). The hub's own state lives on the Z.UIMgr instance (Z.UIMgr.__stellarDH), like
// Dungeon Confirm's stash, so a relog (new Z.UIMgr) starts clean.
//
// Sources (Resources\release_3.7\lua\lua):
//   list    (the catalog half lives in .Lua.Catalog.cs) hero: ChallengeHeroDungeonTable rows grouped by DungeonGroup
//           → StarLevel 1/2/3 = Normal/Hard/Master ids (dungeon_vm.GetHeroDungeonGroup, the NPC's own grouping); a
//           group is shown when one of its DungeonsTable rows has the current season in .Season (fallback tiers when
//           none does — newest season tagged, then function-on; see .Lua.Catalog.cs). Raid:
//           RaidDungeonTable by GroupId, .Difficult 1/2/3, same season test. Activities: fixed FunctionIDs
//           (E.FunctionID WorldBoss / WeeklyHunt / UnionHunt / RogueMain, Trial Path 300301) + ActivityDungeonTable
//           FunctionIds with a live Timer; names/art from recommendedplay_data (SeasonActTable), else FunctionTable.
//           Unstable (v5): own entries, only today's = dungeon_data:GetDungeonList() ∩ this season's
//           NormalHeroDungeonTable rows (no table fallback). Raids: every group the game offers now (+ the current one).
//   unlock  switch.CheckFuncSwitch(DungeonsTable.FunctionID) (the NPC's check, interaction_action.lua:147) and
//           hero_dungeon_main.IsUnlockDungeonId; Master level N is open when N <= GetMasterDungeonMaxDiff + 1
//           (master_challenge_dungeon_diff_item.lua OnSelected); raid difficulty = function on + any boss OpenTimerId
//           in time (raid_main_view.lua:380 checkDungeonAnyBossOpen).
//   solo    hero Normal/Hard: DungeonsTable.SingleModeDungeonId ~= 0 (hero_dungeon_main_view.lua:218/796 — NOT
//           SingleAiMode, which only picks selectType), Master: MasterChallengeDungeonTable.SingleAiMode == 1, raid:
//           DungeonsTable.SingleAiMode ~= 0 (raid_main_view.lua:250); each gated by its SingleAiCondition, silently.
//   match   match_team.IsShowMatchBtn(dungeonId, masterDiff) (raid: no diff, raid_main_view.lua:206).
//   counter hero Normal/Hard: NONE (CountLimit 0 — the game has no counter) → first-clear state instead
//           (completeDungeon[id] == nil + FirstPassAward, hero_dungeon_main_view.lua:1093-1110); Unstable: its
//           CountLimit counter (1); raid: sum over the difficulty's bosses of raid:GetBossCountId; Master:
//           masterModeDungeonInfo[season].masterModeDiffInfo[id].dungeonInfo[diff].completeCount (first clear);
//           World Boss: Z.WorldBoss.WorldBossAwardCountId.
public sealed partial class Plugin
{
    // ── the poll ──────────────────────────────────────────────────────────────────────────────────────────────────
    // Parks __stellarDHSnap = the records (Model.cs) or 'ERR …'. FULL adds the catalog (E/V); SEL = the selected hero
    // dungeon's Master id (its M rows). The pending page open (DhOpen*) is driven from here too: once the page is up
    // and settled it applies our difficulty / mode and reports it in T's openState:
    //   ''  nothing pending · 'W' page not open yet · 'P' page open, settling · 'A <what>' applied · 'E <err>'.
    private static string DhPollChunk(bool full, int sel) =>
        "local FULL = " + (full ? "true" : "false") + " local SEL = " + sel.ToString(CultureInfo.InvariantCulture) + "\n" +
@"pcall(function()
  local m = Z.UIMgr
  if not m then return end
  local st = m.__stellarDH
  if not st then st = {} m.__stellarDH = st end
  rawset(_G, '__stellarDHSnap', nil)
  local ok, res = pcall(function()
    local US, RS = string.char(31), string.char(30)
    local out = {}
    local function clean(v) local s = tostring(v == nil and '' or v) return (s:gsub('%c', ' ')) end
    local function rec(...)
      local t = { ... }
      for i = 1, select('#', ...) do t[i] = clean(t[i]) end
      out[#out + 1] = table.concat(t, US)
    end
    local function b(v) return v and 1 or 0 end
    local function try(f, d) local o, v = pcall(f) if o and v ~= nil then return v end return d end
    local TM = Z.TableMgr
    local DT = TM.GetTable('DungeonsTableMgr')
    local MT = TM.GetTable('MasterChallengeDungeonTableMgr')
    local MAP = require('table.MasterChallenDungeonTableMap')
    local CH = Z.CounterHelper
    local teamVm = Z.VMMgr.GetVM('team')
    local hvm = Z.VMMgr.GetVM('hero_dungeon_main')
    local mtvm = Z.VMMgr.GetVM('match_team')
    local sid = try(function() return Z.VMMgr.GetVM('season').GetCurrentSeasonId() end, nil)
    -- recommended Illusion-Breaking Strength of a DungeonsTable / MasterChallengeDungeonTable row, the game's own rule
    -- (dungeon_vm.lua:621-642 GetRecommendSeasonStrengthText): season_data.CurSeasonId in row.Season AND exactly 3
    -- SeasonLv entries → SeasonLv[3]; else 0 (C# then shows the AS alone). Its own season read, as the game does.
    local ibsSid = try(function() return Z.DataMgr.Get('season_data').CurSeasonId end, nil)
    local function ibsOf(row)
      return try(function()
        if not ibsSid or not row or not row.Season or not table.zcontains(row.Season, ibsSid) then return 0 end
        local lv = row.SeasonLv
        if not lv or #lv ~= 3 then return 0 end
        return tonumber(lv[3]) or 0
      end, 0)
    end
    local function funcOn(fid) return try(function() return Z.VMMgr.GetVM('switch').CheckFuncSwitch(fid) and true or false end, false) end
    local function strip(s) return (tostring(s or ''):gsub('<[^>]*>', '')) end
    -- true, '' when met (or empty); else false + the first unmet condition's text (tags stripped)
    local function condOk(c)
      if not c or #c == 0 then return true, '' end
      if try(function() return Z.ConditionHelper.CheckCondition(c) end, false) then return true, '' end
      local why = ''
      pcall(function()
        for _, v in ipairs(c) do
          local okC, des = Z.ConditionHelper.GetSingleConditionDesc(v[1], v[2], v[3])
          if not okC then why = strip(des) break end
        end
      end)
      return false, why
    end
    local function canMatch(id, diff) return try(function() return mtvm.IsShowMatchBtn(id, diff) and true or false end, false) end
    local function counter(cid)
      if not cid or cid <= 0 then return -1, 0 end
      return try(function() return CH.GetResidueLimitCountByCounterId(cid) end, 0), try(function() return CH.GetCounterLimitCount(cid) end, 0) or 0
    end
    local function diffName(id, diff)
      local r = ''
      pcall(function()
        if diff and diff > 0 then r = MT.GetRow(MAP.DungeonId[id][diff], true).DungeonTypeName return end
        local rr = TM.GetTable('RaidDungeonTableMgr').GetRow(id, true)
        if rr then r = ({ 'Easy', 'Hard', 'Nightmare' })[rr.Difficult] or '' return end
        local ch = TM.GetTable('ChallengeHeroDungeonTableMgr').GetRow(id, true)
        if ch then r = ({ 'Normal', 'Hard' })[ch.StarLevel] or '' end
      end)
      return r
    end
    -- live
    local inTeam = try(function() return teamVm.CheckIsInTeam() end, false)
    local leader = inTeam and try(function() return teamVm.GetYouIsLeader() end, false)
    local cnt, max = 0, 5
    if inTeam then pcall(function()
      local td = Z.DataMgr.Get('team_data')
      cnt = table.zcount(td.TeamInfo.members)
      if td.TeamInfo.baseInfo.teamMemberType == E.ETeamMemberType.Twenty then max = 20 end
    end) end
    local md = Z.DataMgr.Get('match_data')
    local mType = try(function() return md:GetMatchType() end, 0) or 0
    local mDun, mDiff, mAct, mEl, mName, mDiffName = 0, 0, 0, 0, '', ''
    if mType ~= 0 then pcall(function()
      local t0 = md:GetMatchStartTime() or 0
      if t0 > 0 then mEl = math.max(0, math.floor((Z.TimeTools.Now() - t0) / 1000)) end
      if mType == E.MatchType.Team then
        local mtd = Z.DataMgr.Get('match_team_data')
        mDun = mtd:GetCurMatchingDungeonId() or 0
        mDiff = mtd:GetCurMatchingMasterDifficulty() or 0
        local row = DT.GetRow(mDun, true)
        mName = row and row.Name or ''
        mDiffName = diffName(mDun, mDiff)
      elseif mType == E.MatchType.Activity then
        mAct = Z.DataMgr.Get('match_activity_data'):GetActivityId() or 0
        local r = TM.GetTable('SeasonActTableMgr').GetRow(mAct, true)
        mName = r and r.Name or ''
      end
    end) end
    local inDun = try(function() return Z.StageMgr.GetIsInDungeon() and true or false end, false)
    local myFp = try(function() return Z.ContainerMgr.CharSerialize.fightPoint.totalFightPoint end, 0)
    -- my live IBS, Dungeon Confirm's read (Plugin.DungeonConfirm.Lua.cs myIbs; team_hall_view.lua:120)
    local myIbs = tonumber(try(function() return Z.EntityMgr.PlayerEnt:GetLuaAttr(Z.PbAttrEnum('AttrSeasonStrength')).Value end, 0)) or 0
    -- pending page open: apply our preselection once the page is up and has run its own first refresh
    local ost = ''
    local p = st.pend
    if p then
      local okP, err = pcall(function()
        if not m:IsActive(p.view) then ost = 'W' return end
        local v = m:GetView(p.view)
        p.seen = (p.seen or 0) + 1
        -- settled = the page ran its first refresh: hero / raid fill dungeonRow_, the Unstable page dungeonData_
        if not v or p.seen < 2 or (v.dungeonRow_ == nil and v.dungeonData_ == nil) then ost = 'P' return end
        if p.view == 'hero_dungeon_instability_main' then
          -- its solo toggle (digitTog_ → InstabilityIsTeam = false, hero_dungeon_instability_main_view.lua:186)
          if p.solo then v.digitTog_.isOn = true end
          ost = 'A dungeon=' .. tostring(v.dungeonId_) .. ' team=' .. tostring(v.data_ and v.data_.InstabilityIsTeam)
        elseif p.view == 'hero_dungeon_main' then
          if p.stage == 0 then
            if p.level == 3 then v.select = p.diff end
            v:seleLevel(p.level)
            p.stage = 1
            ost = 'P'
            return
          end
          if p.solo then v.digitTog_.isOn = true end
          ost = 'A level=' .. tostring(v.nowLevel) .. ' diff=' .. tostring(v.curMasterDungeonDiff_) .. ' dungeon=' .. tostring(v.dungeonId_) .. ' team=' .. tostring(v.toggleIsTeam_)
        else
          if p.solo then v.uiBinder.tog_digit.isOn = true end
          ost = 'A diff=' .. tostring(v.diff_) .. ' dungeon=' .. tostring(v.dungeonId_) .. ' team=' .. tostring(v.toggleIsTeam_)
        end
        st.pend = nil
      end)
      if not okP then ost = 'E ' .. tostring(err) st.pend = nil end
    end
    rec('T', b(inTeam), b(leader), cnt, max, mType, mDun, mDiff, mAct, mEl, mName, mDiffName, b(inDun), myFp, ost, myIbs)
" + DhWbPollLua + DhGhPollLua + DhCmPollLua + @"
    -- Master levels of the selected hero dungeon
    if SEL > 0 then pcall(function()
      local list = MAP.DungeonId[SEL] or {}
      local maxD = try(function() return hvm.GetMasterDungeonMaxDiff(SEL) end, 0) or 0
      local cfg = DT.GetRow(SEL, true)
      local dunOn = cfg and funcOn(cfg.FunctionID) and try(function() return hvm.IsUnlockDungeonId(SEL) and true or false end, false)
      local done = {}
      pcall(function()
        for d, info in pairs(Z.ContainerMgr.CharSerialize.masterModeDungeonInfo.masterModeDungeonInfo[sid].masterModeDiffInfo[SEL].dungeonInfo) do
          done[d] = (info.completeCount or 0) > 0
        end
      end)
      for diff, mid in ipairs(list) do
        local r = MT.GetRow(mid, true)
        if r then
          local sok, why = condOk(r.SingleAiCondition)
          rec('M', diff, SEL, r.DungeonTypeName, b(dunOn and diff <= maxD + 1), r.RecommendFightValue, b(r.SingleAiMode == 1), b(sok), why,
            b(canMatch(SEL, diff)), 'First clear', done[diff] and 'Cleared' or 'Reward ready', 0, b(diff <= maxD), ibsOf(r))
        end
      end
    end) end
    if not FULL then return table.concat(out, RS) end
" + DhCatalogLua + @"
    return table.concat(out, RS)
  end)
  rawset(_G, '__stellarDHSnap', ok and res or ('ERR ' .. tostring(res)))
end)";

    // ── page opens ────────────────────────────────────────────────────────────────────────────────────────────────
    // Each parks __stellarDHOpen = 'ok' / 'ERR …' / a short result, which C# logs as [DungeonHub].
    private static string Q(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Q(bool v) => v ? "true" : "false";

    // Hero Normal / Hard / Master: replay the NPC (interaction_action.lua HeroChallengeDungeon :142-168) — the group's
    // StarLevel table into hero_dungeon_main_data, FunctionId = Challenge, GoToFunc(DungeonsTable.FunctionID) — then
    // leave a pend for the poll: level 1/2/3 (the page's own tab key), Master diff (its `select`, read by
    // initMasterDiffLoop), and Solo (its digit toggle). A page that is already up is closed first (it reads the
    // group only on open).
    private static string DhOpenHeroChunk(int group, int level, int diff, bool solo) => DhOpenWrap(
        " local grp = Z.VMMgr.GetVM('dungeon').GetHeroDungeonGroup(" + Q(group) + ")" +
        " if not grp or table.zcount(grp) == 0 or not grp[1] then error('no such dungeon group') end" +
        " local row = Z.TableMgr.GetTable('DungeonsTableMgr').GetRow(grp[1].DungeonId)" +
        " local on, reason = Z.VMMgr.GetVM('switch').CheckFuncSwitch(row.FunctionID)" +
        " if not on then if reason and reason[1] then Z.TipsVM.OpenViewById(reason[1].error, reason[1].params) end error('function locked') end" +
        " local hd = Z.DataMgr.Get('hero_dungeon_main_data')" +
        " if m:IsActive('hero_dungeon_main') then m:CloseView('hero_dungeon_main') end" +
        " hd:SetChallengeScenceId(grp)" +
        " hd:SetFunctionId(Z.PbEnum('EFunctionType', 'FunctionTypeHeroDungeonChallenge'))" +
        " local went = Z.VMMgr.GetVM('gotofunc').GoToFunc(row.FunctionID)" +
        " st.pend = { view = 'hero_dungeon_main', level = " + Q(level) + ", diff = " + Q(diff) + ", solo = " + Q(solo) + ", stage = 0, seen = 0 }" +
        " res = 'GoToFunc(' .. tostring(row.FunctionID) .. ') = ' .. tostring(went)");

    // Raid: the raid VM's own opener preselects the difficulty (raid_vm.lua:7-17); Solo = its digit toggle (pend).
    private static string DhOpenRaidChunk(int group, int diff, bool solo) => DhOpenWrap(
        " if not Z.VMMgr.GetVM('raid'):CheckAnyDiffRaidDungeonOpen(" + Q(group) + ", true) then error('raid closed') end" +
        " Z.VMMgr.GetVM('raid'):OpenRaidMainView(" + Q(group) + ", " + Q(diff) + ")" +
        " st.pend = { view = 'raid_main', solo = " + Q(solo) + ", stage = 0, seen = 0 }" +
        " res = 'OpenRaidMainView'");

    // Unstable: replay its NPC (interaction_action.lua HeroNormalDungeon :118-140) — SetScenceId(the DUNGEON id the NPC
    // resolves from its GetDungeonList index; the page reads ScenceId as a dungeon id, initBaseData), FunctionId =
    // HeroDungeonNormal (300201), GoToFunc(DungeonsTable.FunctionID) — then a pend for Solo (its digit toggle).
    private static string DhOpenUnstableChunk(int dungeonId, bool solo) => DhOpenWrap(
        " local row = Z.TableMgr.GetTable('DungeonsTableMgr').GetRow(" + Q(dungeonId) + ")" +
        " local on, reason = Z.VMMgr.GetVM('switch').CheckFuncSwitch(row.FunctionID)" +
        " if not on then if reason and reason[1] then Z.TipsVM.OpenViewById(reason[1].error, reason[1].params) end error('function locked') end" +
        " local hd = Z.DataMgr.Get('hero_dungeon_main_data')" +
        " if m:IsActive('hero_dungeon_instability_main') then m:CloseView('hero_dungeon_instability_main') end" +
        " hd:SetScenceId(" + Q(dungeonId) + ")" +
        " hd:SetFunctionId(Z.PbEnum('EFunctionType', 'FunctionTypeHeroDungeonNormal'))" +
        " local went = Z.VMMgr.GetVM('gotofunc').GoToFunc(row.FunctionID)" +
        " st.pend = { view = 'hero_dungeon_instability_main', solo = " + Q(solo) + ", stage = 0, seen = 0 }" +
        " res = 'GoToFunc(' .. tostring(row.FunctionID) .. ') = ' .. tostring(went)");

    // Activities: the game's generic entry (lock / scene / dead checks + FunctionTable.FunctionOrder), no preselect.
    private static string DhOpenFuncChunk(int funcId) => DhOpenWrap(
        " res = 'GoToFunc(" + Q(funcId) + ") = ' .. tostring(Z.VMMgr.GetVM('gotofunc').GoToFunc(" + Q(funcId) + "))");

    // Fallback when no page came up: walk to the dungeon's NPC, as the game's own "go there" (match_team_vm.lua:32-36).
    private static string DhWalkChunk(int dungeonId, int diff) => DhOpenWrap(
        " st.pend = nil" +
        " local tid = Z.VMMgr.GetVM('team_main').GetTargetIdByDungeonId(" + Q(dungeonId) + ", " + Q(diff) + ")" +
        " local row = tid and Z.TableMgr.GetTable('TeamTargetTableMgr').GetRow(tid, true)" +
        " if not row then error('no team target for dungeon " + Q(dungeonId) + "') end" +
        " Z.VMMgr.GetVM('quick_jump').DoJumpByConfigParam(row.QuickJumpType, row.QuickJumpParam)" +
        " res = 'quick_jump target=' .. tostring(tid) .. ' type=' .. tostring(row.QuickJumpType)");

    private static string DhClearPendChunk =>
        "pcall(function() local st = Z.UIMgr and Z.UIMgr.__stellarDH if st then st.pend = nil end end)";

    private static string DhOpenWrap(string body) =>
        "pcall(function() rawset(_G, '__stellarDHOpen', nil)" +
        " local m = Z.UIMgr local st = m.__stellarDH if not st then st = {} m.__stellarDH = st end local res = 'ok'" +
        " local ok, err = pcall(function()" + body + " end)" +
        " rawset(_G, '__stellarDHOpen', ok and res or ('ERR ' .. tostring(err))) end)";

    // ── match ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Team: the same call the hero / raid / Unstable pages make (hero_dungeon_main_view.lua:286 with difficulty = the Master
    // diff, 0 on Normal/Hard; raid_main_view.lua:141 and hero_dungeon_instability_main_view.lua:94 with no difficulty). It runs match_team TryBeginMatch: condition, leader,
    // ability score, class quota and the game's dialogs (or the change-match dialog when already matching).
    private static string DhMatchTeamChunk(int dungeonId, int diff, bool noDiff) => DhOpenWrap(
        " local tok = Z.DataMgr.Get('match_data').CancelSource:CreateToken()" +
        " Z.VMMgr.GetVM('match').RequestBeginMatch(E.MatchType.Team, { dungeonId = " + Q(dungeonId) +
        (noDiff ? "" : ", difficulty = " + Q(diff)) + " }, tok)" +
        " res = 'RequestBeginMatch(Team, " + Q(dungeonId) + ", " + Q(diff) + ")'");

    // Activity (World Boss) match: Plugin.DungeonHub.WorldBoss.cs DhWbMatchChunk (timer, leader, boss-info, counter).

    // Cancel: match_team CancelMatchDialog's leader check (tip 1000645 for a member), then AsyncCancelMatch in a
    // coroutine (it is an RPC). Its YesNo dialog is skipped: the decompile there is garbled, and our red Cancel pill is
    // already an explicit press.
    private static string DhCancelChunk => DhOpenWrap(
        " local tv = Z.VMMgr.GetVM('team')" +
        " if tv.CheckIsInTeam() and not tv.GetYouIsLeader() then Z.TipsVM.ShowTips(1000645) res = 'leader only' return end" +
        " Z.CoroUtil.create_coro_xpcall(function() Z.VMMgr.GetVM('match').AsyncCancelMatch() end)()" +
        " res = 'AsyncCancelMatch'");
}
