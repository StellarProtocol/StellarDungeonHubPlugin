namespace Stellar.DungeonHub;

// Dungeons hub — the catalog half of the poll chunk (Plugin.DungeonHub.Lua.cs DhPollChunk splices it in after the
// live T / M records, only on a FULL poll): the E / V records of every tab. Split out of .Lua.cs for the 500-line gate.
// It runs INSIDE the poll's pcall'd function, so it sees the poll's locals (rec, b, try, funcOn, condOk, canMatch,
// counter, ibsOf, DT, MT, MAP, TM, hvm, sid, out, RS). ⚠️ A C# @"…" verbatim string: no double quote anywhere in the Lua,
// comments included (one ends the string — CS1010 storm, Dungeon-Access-Hub.md §8).
public sealed partial class Plugin
{
    private const string DhCatalogLua = @"
    local function inSeason(row) return row and row.Season and sid and table.zcontains(row.Season, sid) end
    -- hero dungeons (tab 0)
    local groups, order = {}, {}
    for _, r in pairs(TM.GetTable('ChallengeHeroDungeonTableMgr').GetDatas()) do
      local g = groups[r.DungeonGroup]
      if not g then g = {} groups[r.DungeonGroup] = g order[#order + 1] = r.DungeonGroup end
      g[r.StarLevel] = r
      if inSeason(DT.GetRow(r.DungeonId, true)) then g.cur = true end
    end
    table.sort(order)
    -- a group's BASE row = its lowest star level present: Normal (1), else Hard (2). Season 4 (CN) groups have ONLY
    -- Hard + Master (StarLevel 2 / 3, no Normal), so anchoring on g[1] dropped every one of them from the list.
    -- The name, thumb fallback, lock state, cap and FunctionID come from it.
    local function base(g) return g[1] or g[2] end
    -- Unstable (v5: their own list entries, at the top): a SEPARATE dungeon set ('Unstable Space - …', PlayType 8,
    -- NormalHeroDungeonTable), not a difficulty. Listed = ONLY the ones open today: the server's daily set
    -- (dungeon_data:GetDungeonList(), refreshed by DhRefreshUnstableChunk on every hub open, dungeon_vm.lua:210-223)
    -- intersected with this season's NormalHeroDungeonTable rows. No fallback to the whole table when the server list
    -- is empty: that would list closed ones. Name = the regular dungeon's (known season-3 team ids first, else the
    -- group whose Normal name the Unstable name contains), title = the full DungeonsTable name.
    local NT = TM.GetTable('NormalHeroDungeonTableMgr')
    local KNOWN = { [6512] = 13, [6522] = 14, [6542] = 16, [6562] = 18, [1621] = 15, [1153] = 17 }
    local dvm = Z.VMMgr.GetVM('dungeon')
    local live = try(function() return Z.DataMgr.Get('dungeon_data'):GetDungeonList() end, nil)
    local useen = {}
    if type(live) == 'table' then for _, id in ipairs(live) do
      local nr = NT.GetRow(id, true)
      local d = nr and nr.SeasonId == sid and not useen[id] and DT.GetRow(id, true)
      if d then
        useen[id] = true
        local gid = KNOWN[id]
        if not gid then
          local best = 0
          for _, og in ipairs(order) do
            local g1 = base(groups[og])
            local gn = g1 and groups[og].cur and DT.GetRow(g1.DungeonId, true)
            if gn and #gn.Name > best and string.find(d.Name, gn.Name, 1, true) then gid = og best = #gn.Name end
          end
        end
        local g1 = gid and groups[gid] and base(groups[gid])
        local gd = g1 and DT.GetRow(g1.DungeonId, true)
        local name = (gd and gd.Name and gd.Name ~= '') and gd.Name or d.Name
        -- the Unstable page (hero_dungeon_instability_main_view.lua): solo = SingleAiMode 1 + the solo row's
        -- SingleAiCondition (:308 refreshUi), Match = IsShowMatchBtn(teamId) with no difficulty (:620), rewards = its
        -- own DungeonsTable.CountLimit counter (1, one count shared by every Unstable dungeon), no fallback
        local srow = DT.GetRow(d.SingleModeDungeonId or 0, true) or d
        local on = funcOn(d.FunctionID) and condOk(d.Condition)
        local sok, why = condOk(srow.SingleAiCondition)
        local ur, ul = counter(d.CountLimit or 0)
        local uval = ur >= 0 and (tostring(ur) .. ' / ' .. tostring(ul)) or ''
        -- Recommended = the page's own text (:643 GetRecommendSeasonStrengthText(showDungeonRow_): the team row, or the
        -- solo row in solo mode), NOT RecommendFightValue (100 on these rows); '' when the game has none
        local rtext = strip(try(function() return dvm.GetRecommendSeasonStrengthText(d) end, ''))
        if rtext == '' then rtext = strip(try(function() return dvm.GetRecommendSeasonStrengthText(srow) end, '')) end
        local cap = try(function() return tonumber(d.LimitedNum[2]) end, 0) or 0
        local key = 'u' .. id
        rec('E', 0, key, name, nr.Background or '', '', b(not on), not on and 'Locked' or '', cap, gid or 0, 0, 0, 0, '', 0, '', '', 0, 0, 0, 1, d.Name)
        rec('V', key, 0, id, 0, 'Unstable', b(on), 0, b(d.SingleAiMode == 1), b(sok), why,
          b(canMatch(id, nil)), 'Rewards left', uval, b(ur == 0 and ul > 0), nr.Background or '', '', 8, rtext, -1)
      end
    end end
    -- regular hero dungeons. Normal / Hard have NO reward counter (CountLimit 0, SingleAwardCounterId 0): the third
    -- tile is the first-clear reward, as the hero page decides it (hero_dungeon_main_view.lua:1093-1110: shown while
    -- NOT CheckDungeonIsComplete(id) = completeDungeon[id] == nil, and FirstPassAward is not empty). fc: 1 ready,
    -- 2 claimed, 0 no first-clear reward. List thumb = the Master row's art when the group has one (v5).
    local complete = try(function() return Z.ContainerMgr.CharSerialize.dungeonList.completeDungeon end, nil)
    local function firstClear(id, row)
      if complete and try(function() return complete[id] ~= nil end, false) then return 2 end
      if try(function() return #row.FirstPassAward > 0 end, false) then return 1 end
      return 0
    end
    -- which hero groups to list, in tiers (the CN client, season 4, listed NONE with tier 1 alone):
    --   1) groups with a DungeonsTable row whose .Season holds the current season (g.cur) — always hits on SEA S3;
    --   2) none: the NEWEST season any group's rows carry in .Season (max over every row), groups tagged with it;
    --   3) still none (no season data at all): groups whose base row exists and whose FunctionID is on.
    -- Tiers 2 / 3 only run when tier 1 finds nothing, so the SEA poll costs the same as before.
    local show, any = {}, false
    for _, gid in ipairs(order) do if groups[gid].cur then show[gid] = true any = true end end
    if not any then
      local maxS, gs = nil, {}
      for _, gid in ipairs(order) do
        local set = {}
        gs[gid] = set
        for star, r in pairs(groups[gid]) do
          if type(star) == 'number' then pcall(function()
            for _, v in pairs(DT.GetRow(r.DungeonId, true).Season) do
              local s = tonumber(v)
              if s then set[s] = true if not maxS or s > maxS then maxS = s end end
            end
          end) end
        end
      end
      if maxS then
        for _, gid in ipairs(order) do if gs[gid][maxS] then show[gid] = true end end
      else
        for _, gid in ipairs(order) do
          local g1 = base(groups[gid])
          local d = g1 and DT.GetRow(g1.DungeonId, true)
          if d and funcOn(d.FunctionID) then show[gid] = true end
        end
      end
    end
    for _, gid in ipairs(order) do
      local g = groups[gid]
      local n = base(g)
      local nd = n and DT.GetRow(n.DungeonId, true)
      if show[gid] and nd then
        local key = 'g' .. gid
        local mid = g[3] and g[3].DungeonId or 0
        local nOn = funcOn(nd.FunctionID) and try(function() return hvm.IsUnlockDungeonId(n.DungeonId) and true or false end, false)
        local cap = try(function() return tonumber(nd.LimitedNum[2]) end, 0) or 0
        local mCount = (mid > 0 and MAP.DungeonId[mid]) and #MAP.DungeonId[mid] or 0
        local mMax = mid > 0 and try(function() return hvm.GetMasterDungeonMaxDiff(mid) end, 0) or 0
        local mArt = g[3] and g[3].Background or ''
        -- Master unlocked (the M rows' dunOn rule), for the list sub-line: the highest difficulty the player has open
        local mrow = mid > 0 and DT.GetRow(mid, true)
        local mOn = mrow and funcOn(mrow.FunctionID) and try(function() return hvm.IsUnlockDungeonId(mid) and true or false end, false) or false
        local thumb = mArt ~= '' and mArt or n.Background
        rec('E', 0, key, nd.Name, thumb, '', b(not nOn), not nOn and 'Locked' or '', cap, gid, mid, mCount, mMax, mArt, 0, '', '', 0, 0, 0, 0, '', b(mOn))
        for star = 1, 2 do
          local r = g[star]
          local d = r and DT.GetRow(r.DungeonId, true)
          if d then
            local on = funcOn(d.FunctionID) and try(function() return hvm.IsUnlockDungeonId(r.DungeonId) and true or false end, false)
            local sok, why = condOk(d.SingleAiCondition)
            local fc = firstClear(r.DungeonId, d)
            rec('V', key, star, r.DungeonId, 0, star == 1 and 'Normal' or 'Hard', b(on), d.RecommendFightValue, b((d.SingleModeDungeonId or 0) ~= 0), b(sok), why,
              b(canMatch(r.DungeonId, 0)), 'First clear', fc == 1 and 'Reward ready' or fc == 2 and 'Claimed' or '', 0, r.Background, '', star, '', fc, ibsOf(d))
          end
        end
      end
    end
    -- raids (tab 1): every RaidDungeonTable group the game offers right now, not a hard-coded list. Shown = the current
    -- season's raid (even while closed, as Locked) + any other group with a difficulty open = function on and a boss
    -- in its OpenTimerId — raid_vm CheckAnyDiffRaidDungeonOpen minus its scene / buff / dead part, so the list doesn't
    -- flicker while standing in a dungeon (the Enter re-checks the full one). Brings the Backtrack groups (31 / 32,
    -- Season NOT filtered) while the game runs them. Current season first, then by GroupId.
    local RB = TM.GetTable('RaidBossTableMgr')
    local rvm = Z.VMMgr.GetVM('raid')
    local rg, rorder, rcur = {}, {}, {}
    for _, r in pairs(TM.GetTable('RaidDungeonTableMgr').GetDatas()) do
      if not rg[r.GroupId] then rg[r.GroupId] = {} rorder[#rorder + 1] = r.GroupId end
      rg[r.GroupId][r.Difficult] = r
      if inSeason(DT.GetRow(r.DungeonId, true)) then rcur[r.GroupId] = true end
    end
    table.sort(rorder, function(a, c)
      local ka, kc = rcur[a] and 0 or 1, rcur[c] and 0 or 1
      if ka ~= kc then return ka < kc end
      return a < c
    end)
    for _, gid in ipairs(rorder) do
      local g = rg[gid]
      local first = g[1] or g[2] or g[3]
      local key = 'r' .. gid
      local fd = DT.GetRow(first.DungeonId, true)
      local nDiff = (g[1] and 1 or 0) + (g[2] and 1 or 0) + (g[3] and 1 or 0)
      local rows, anyOn = {}, false
      for diff = 1, 3 do
        local r = g[diff]
        local d = r and DT.GetRow(r.DungeonId, true)
        if d then
          local on = funcOn(d.FunctionID) and try(function()
            for _, boss in ipairs(r.BossId) do
              local br = RB.GetRow(boss, true)
              if br and Z.TimeTools.CheckIsInTimeByTimeId(br.OpenTimerId) then return true end
            end
            return false
          end, false)
          local rem, lim = 0, 0
          pcall(function()
            for _, boss in ipairs(r.BossId) do
              local cid = rvm:GetBossCountId(boss)
              if cid and cid > 0 then local a, l2 = counter(cid) rem = rem + math.max(a, 0) lim = lim + l2 end
            end
          end)
          local cval = lim > 0 and (tostring(rem) .. ' / ' .. tostring(lim)) or ''
          if on then anyOn = true end
          local sok, why = condOk(d.SingleAiCondition)
          -- a one-difficulty group (Backtrack) has no Easy/Hard/Nightmare choice: its one chip reads 'Normal'
          local label = nDiff == 1 and 'Normal' or ({ 'Easy', 'Hard', 'Nightmare' })[diff]
          rows[#rows + 1] = { 'V', key, diff - 1, r.DungeonId, 0, label, b(on), d.RecommendFightValue or 0,
            b((d.SingleAiMode or 0) ~= 0), b(sok), why, b(canMatch(r.DungeonId, nil)), 'Rewards left', cval, b(lim > 0 and rem == 0),
            r.MonsterShowTexture or '', r.DiffBgTexture or '', ({ 1, 2, 4 })[diff], '', -1, ibsOf(d) }
        end
      end
      if rcur[gid] or anyOn then
        -- name: a multi-difficulty group shares RaidDungeonTable.Name; a Backtrack group's DungeonsTable name carries
        -- the 'Backtrack!' tag the game shows ('Backtrack! Dreambloom Ruins')
        local name = (nDiff == 1 and fd and fd.Name and fd.Name ~= '') and fd.Name or first.Name
        local cap = try(function() return tonumber(fd.LimitedNum[2]) end, 0) or 0
        rec('E', 1, key, name, first.MonsterShowTexture, first.DiffBgTexture, b(not anyOn), anyOn and '' or 'Locked', cap, gid, 0, 0, 0, '', 0, '', '', 0, 0, 0)
        for _, v in ipairs(rows) do rec(table.unpack(v)) end
      end
    end
    -- activities (tabs 2-4)
    local rpd = Z.DataMgr.Get('recommendedplay_data')
    local FT = TM.GetTable('FunctionTableMgr')
    -- Art fallback when the recommended-play config has none: that map only holds THIS season's SeasonActTable rows,
    -- and some activities only have an older season's row (Slumberdream Valley = RogueMain 302000: row 45, season 2).
    -- 1) RogueMain: its own page background = this season's lowest-Difficult RogueTable row's DiffBgTexture
    --    (rouge_main_view refreshTabs/refreshPanel: rouge_data season rows, nowLevel 1);
    -- 2) any season's SeasonActTable row for the function (newest season first), BackGroundPic;
    -- 3) FunctionTable.PreviewPic. Empty when none exists (the box keeps its empty tile).
    local function actArt(fid)
      if fid == E.FunctionID.RogueMain then
        local p = try(function()
          local best
          for _, r in pairs(TM.GetTable('RogueTableMgr').GetDatas()) do
            if r.Season == sid and (r.DiffBgTexture or '') ~= '' and (not best or r.Difficult < best.Difficult) then best = r end
          end
          return best and best.DiffBgTexture
        end, nil)
        if p then return p end
      end
      local p = try(function()
        local best
        for _, r in pairs(TM.GetTable('SeasonActTableMgr').GetDatas()) do
          if r.FunctionId == fid and (r.BackGroundPic or '') ~= '' and (not best or (r.SeasonId or 0) > (best.SeasonId or 0)) then best = r end
        end
        return best and best.BackGroundPic
      end, nil)
      if p then return p end
      return try(function() return FT.GetRow(fid, true).PreviewPic end, '')
    end
    local function act(tab, fid, art)
      local cfg = try(function() return rpd:GetRecommendedPlayConfigByFunctionId(fid) end, nil)
      local name = cfg and cfg.Name or try(function() return FT.GetRow(fid, true).Name end, '')
      if name == '' then return end
      if (not art or art == '') and cfg then art = cfg.BackGroundPic end
      if not art or art == '' then art = actArt(fid) end
      local on = funcOn(fid)
      local ck, cv, cz, cm, aid = '', '', 0, 0, 0
      if fid == E.FunctionID.WorldBoss then
        local rem, lim = counter(Z.WorldBoss.WorldBossAwardCountId)
        if rem >= 0 then ck = 'Rewards left' cv = tostring(rem) .. ' / ' .. tostring(lim) cz = b(rem == 0 and lim > 0) end
        if cfg and cfg.Id then cm = 1 aid = cfg.Id end
      end
      local sub = not on and 'Locked' or ''
      rec('E', tab, 'a' .. fid, name, art or '', '', b(not on), sub, 0, 0, 0, 0, 0, '', fid, ck, cv, cz, cm, aid)
    end
    act(2, E.FunctionID.WorldBoss)
    act(3, E.FunctionID.WeeklyHunt)
    act(3, E.FunctionID.UnionHunt)
    -- Bureau Commissions (800100) first on the Other tab: its name / art = the recommended-play row (SeasonActTable 10,
    -- BackGroundPic activity_weituo); its live state is the C record (.Commissions.cs)
    act(4, E.FunctionID.WorldEvent)
    act(4, 300301)
    act(4, E.FunctionID.RogueMain)
    pcall(function()
      local seen, list = {}, {}
      for _, r in pairs(TM.GetTable('ActivityDungeonTableMgr').GetDatas()) do
        if not seen[r.FunctionId] and try(function() return Z.TimeTools.CheckIsInTimeByTimeId(r.Timer) end, false) then
          seen[r.FunctionId] = r.Background or '' list[#list + 1] = r.FunctionId
        end
      end
      table.sort(list)
      for _, fid in ipairs(list) do act(4, fid, seen[fid]) end
    end)
";

    // Hub open: ask the server for today's Unstable set again (the game's own refresh, dungeon_vm.lua:210-223 — an RPC,
    // so in a coroutine; it fills dungeon_data's list, which the next FULL poll re-reads). Fire-and-forget.
    private const string DhRefreshUnstableChunk =
        "pcall(function() Z.CoroUtil.create_coro_xpcall(function() Z.VMMgr.GetVM('dungeon').AsyncGetSeasonDungeonList() end)() end)";
}
