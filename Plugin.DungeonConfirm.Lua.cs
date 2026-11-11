namespace Stellar.DungeonHub;

// Dungeon Confirm — every Lua chunk. Game facts: Knowledge Base\Dungeon-Confirm-Replacement.md.
//
// Interception: the game opens the confirm screen ONLY through Z.UIMgr:OpenView("team_enter", ...) — team vote
// (team_enter_vm.lua:13 OpenEnterView, also reached from the scene-change re-open team_vm.lua:1129 →
// HandleTeamActivity) and match found (match_team_vm.lua:51). It closes ONLY through Z.UIMgr:CloseView("team_enter")
// (team_enter_vm.lua:18, match_team_vm.lua:56). So, like Plugin.DungeonResult.cs, we wrap those two methods ONCE on the
// Z.UIMgr instance (never _G: strict-global guard) behind a boolean flag, and never unwrap. OpenView with the flag on
// stores the request in the stash table Z.UIMgr.__stellarDC instead of opening the view; CloseView always marks the
// stash closed and then runs the original. The view holds no protocol logic (replies/state live in the VMs), so not
// opening it is safe.
//
// ⚠️ Chaining with Plugin.DungeonResult.cs: both wrap Z.UIMgr.OpenView, each with its OWN install guard
// (__stellarDCInstalled / __stellarResultSuppressInstalled) and each capturing whatever OpenView is current at install
// time, then tail-calling it for keys it doesn't own. Either install order yields one chain that reaches the game's
// original, and neither ever restores an "original" (which would drop the other's wrapper).
//
// Lua never calls C# (native Lua→C# callbacks crash this game). The vote / "doing" events are subscribed IN LUA and
// only write into the stash; C# polls the stash (Plugin.DungeonConfirm.cs) through ILua park-and-read: the chunk
// rawsets two strings on _G and C# reads them with ReadGlobalString (Framework-Interop-Foundation.md §ILua).
public sealed partial class Plugin
{
    // Who declined. A member's refuse arrives as GrpcTeamNtf.TeamActivityVoteResult{code: E.TeamVoteRet, vCharId}
    // (grpc_team_ntf_impl.lua:328), and NotifyTeamActivityState(state No) follows at once and closes the view via
    // CloseView. The impl's TeamRefreshActivityVoteResult dispatch (our readyMap listener) sits in a decompile-garbled
    // branch that may not run for Refuse, and the close beats our 200 ms poll, so the window ended on a bare
    // "Entry cancelled". So we hook the notify STUB itself, one level below the VM.
    //
    // Reaching the stub at runtime: grpc_team_ntf_gen.lua:7 does `local impl = require("zservice/grpc_team_ntf_impl")`
    // and dispatches every method as `impl:TeamActivityVoteResult(call, pbMsg.vRequest)` (:186) — a field lookup on the
    // module table at CALL time, so replacing the field on package.loaded's table takes effect. ⚠️ Same require key
    // (slash separator) as the game, or we'd get a second copy of the module (Lua-Injection-from-CSharp.md, require
    // separator). The wrappers record, then ALWAYS tail-call the original; own install guard on the impl table (it
    // survives a Z.UIMgr re-create, and a fresh Lua state brings a fresh table with no flag). Reinstalled from the poll
    // too, so a module first loaded after our install still gets hooked.
    //
    // Records, all on the stash and keyed by the request seq so CloseView never wipes them (the C# poll after the close
    // still reads who refused): votes[seq][charId] = code letter (A agree, R refuse, C cancel, T timeout, N none),
    // lastVote, closeRefuse[seq] = the closing TeamActivity's refuseId (stru_team_activity.proto:18), and voteQ — a
    // queue the poll drains into the head for C#'s one-log-line-per-vote. A refuse also sets readyMap[charId] = false
    // directly, so a poll that lands before the close already shows the row as Declined.
    private const string DcVoteHook =
@"pcall(function()
  local impl = package.loaded['zservice/grpc_team_ntf_impl'] or require('zservice/grpc_team_ntf_impl')
  if type(impl) ~= 'table' or impl.__stellarDCVote then return end
  local origVote, origState = impl.TeamActivityVoteResult, impl.NotifyTeamActivityState
  if type(origVote) ~= 'function' or type(origState) ~= 'function' then return end
  impl.__stellarDCVote = true
  impl.TeamActivityVoteResult = function(self, call, req, ...)
    pcall(function()
      local st = Z.UIMgr and Z.UIMgr.__stellarDC
      if not st or not req then return end
      local R = E.TeamVoteRet
      local c = req.code
      local l = (c == R.Agree and 'A') or (c == R.Refuse and 'R') or (c == R.Cancel and 'C') or (c == R.TimeOut and 'T') or 'N'
      local cid = req.vCharId
      st.votes = st.votes or {}
      st.votes[st.seq] = st.votes[st.seq] or {}
      if cid ~= nil then st.votes[st.seq][cid] = l end
      st.lastVote = { charId = cid, code = c, seq = st.seq }
      st.voteQ = st.voteQ or {}
      st.voteQ[#st.voteQ + 1] = tostring(cid) .. ':' .. tostring(c) .. ':' .. l .. ':' .. tostring(st.seq)
      if l == 'R' and cid ~= nil and st.readyMap then st.readyMap[cid] = false end
    end)
    return origVote(self, call, req, ...)
  end
  impl.NotifyTeamActivityState = function(self, call, req, ...)
    pcall(function()
      local st = Z.UIMgr and Z.UIMgr.__stellarDC
      local a = req and req.state
      if st and a and a.state == Z.PbEnum('ETeamActivityState', 'ETeamActivity_No') then
        st.closeRefuse = st.closeRefuse or {}
        st.closeRefuse[st.seq] = a.refuseId or 0
      end
    end)
    return origState(self, call, req, ...)
  end
end)
";

    // Install both wrappers + the event listeners (each once, idempotent) and set the on/off flag. Turning OFF while an
    // intercepted request is still pending hands it to the game's own view, so the player is never left without one.
    private void DcSetFlag(bool on) => _services.Lua.DoString(DcVoteHook +
@"pcall(function()
  local m = Z.UIMgr
  if not m then return end
  if not m.__stellarDCInstalled then
    local origOpen, origClose = m.OpenView, m.CloseView
    m.__stellarDCOrigOpenView = origOpen
    m.__stellarDC = { seq = 0, open = false, native = false, readyMap = {} }
    m.OpenView = function(self, key, viewData, ...)
      if key == 'team_enter' and self.__stellarDCOn then
        local st = self.__stellarDC
        st.seq = st.seq + 1
        st.open = true
        st.native = false
        st.viewData = viewData
        st.isMatching = (viewData and viewData.isMatching) and true or false
        st.readyMap = {}
        -- vote records are per seq (DcVoteHook) and survive the close; just drop ones a few requests old
        if st.votes then st.votes[st.seq - 4] = nil end
        if st.closeRefuse then st.closeRefuse[st.seq - 4] = nil end
        st.doing = false
        st.info = nil
        return
      end
      return origOpen(self, key, viewData, ...)
    end
    m.CloseView = function(self, key, ...)
      -- Only 'open' flips: votes / closeRefuse are keyed by seq and stay, so the poll after the close sees who refused.
      if key == 'team_enter' and self.__stellarDC then self.__stellarDC.open = false end
      return origClose(self, key, ...)
    end
    m.__stellarDCInstalled = true
  end
  -- Same events team_enter_view BindEvents listens to (team_enter_view.lua:801-805), with the same Add(evt, fn, obj)
  -- shape: the dispatcher calls fn(obj, ...) (event_dispatcher.lua:7-13). Guard + owner live on the EventMgr instance.
  local ev = Z.EventMgr
  if ev and not ev.__stellarDCEvt then
    local owner = {}
    ev.__stellarDCEvt = owner
    ev:Add(Z.ConstValue.Team.TeamRefreshActivityVoteResult, function(_, d)
      local st = m.__stellarDC
      if st and st.open and d and d.charId ~= nil then st.readyMap[d.charId] = d.isAgree and true or false end
    end, owner)
    ev:Add(Z.ConstValue.Team.HideActivityLeaderCancelBtn, function()
      local st = m.__stellarDC
      if st and st.open then st.doing = true end
    end, owner)
    ev:Add(Z.ConstValue.Match.MatchPlayerInfoChange, function()
      local st = m.__stellarDC
      if st then st.matchRev = (st.matchRev or 0) + 1 end
    end, owner)
  end
  local on = " + (on ? "true" : "false") + @"
  local st = m.__stellarDC
  if not on and m.__stellarDCOn and st and st.open and not st.native then
    st.native = true
    m.__stellarDCOrigOpenView(m, 'team_enter', st.viewData)
  end
  m.__stellarDCOn = on
end)");

    // The poll. Parks __stellarDCHead = 'id|seq|open|native|refuser|end|votes|timeouts|agrees' (id = the stash table's address, so a
    // re-created Z.UIMgr after relog never collides with an old seq; 'X' = not installed) and, while open,
    // __stellarDCSnap = the snapshot (format in Plugin.DungeonConfirm.Model.cs) or 'ERR…'. The snapshot body has its own
    // pcall so the head always lands. The head's tail is built EVEN WHEN CLOSED — that's how C# learns who refused after
    // the game's close (DcVoteHook):
    //   refuser = charId of a Refuse vote for this seq; else the closing TeamActivity's refuseId; else (closed only)
    //             viewData.refuseId; non-zero only, '' = unknown.
    //   end     = 'R' if anyone refused, else 'C' / 'T' if a Cancel / TimeOut vote arrived, else ''.
    //   votes   = the drained vote queue, 'charId:code:letter:seq' joined by ';' (C# logs one line each).
    //   timeouts = charIds with a TimeOut vote for this seq (one per member who didn't answer; the leader sends none),
    //             joined by ',' — the "Time ran out · <Name> didn't answer" header + their "No answer" rows.
    //   agrees  = charIds whose latest vote for this seq is Agree, joined by ',' — the last Agrees often land in the
    //             same poll as the close, so C# marks those rows Ready before deciding "all in" vs a failure.
    //
    // Sources, all as team_enter_view reads them:
    //   team:  dungeon = viewData.assignSceneParams.sceneId, diff = .initParam.masterModeDiff (setViewInfo :202/:210);
    //          leader / twenty from team_data.TeamInfo.baseInfo (:260-262); members = team VM GetTeamMemData() (:446);
    //          name/level/profession from socialData.basicData / professionData, bots via BotAITableMgr + leader level
    //          (setHalfPortraitPImg :596-624); assist = awardCountInfo[charId].isAssist (:480); 30 s = Z.Global.
    //          TeamApplyActivityLastTime (:324); key = KeyRewardLimitId limit − leader's keyAwardCount (:486-489).
    //   match: dungeon/diff/target from match_team_data (setMatchTeamViewInfo :88-96); five vs twenty =
    //          MemberCountStopMatch <= 5 (:101); time = MatchTable[MatchID].ConfirmTime (:134-135); players from
    //          match_data (setMatchMemberInfo :362, readyStatus vs E.RedayType :377; names :689/:702).
    //          ⚠️ read md.matchPlayerInfo_ directly: GetMatchPlayerInfo() logGreen()s on every call (match_data.lua:115).
    //   layout (header 'twenty' field = the 4-group layout): the TEAM — team mode teamMemberType Twenty, match mode the
    //          five/twenty rule above. A 20-person party in a 5-player dungeon still gets its 4 groups (user, 2026-11-06;
    //          58f44fe briefly chose the layout by the dungeon cap, which dropped members past the 5th).
    //   raidContent (last header field): only the 4-group panel's caption — RAID when the DUNGEON is a raid, PARTY when a
    //          20-person party entered a dungeon (Mistveil 6545 with a Twenty team said "RAID", 2026-11-06). Cap =
    //          DungeonsTable.LimitedNum[2] (the {min, max} pair the hero/raid menus read as minCount/maxCount,
    //          hero_dungeon_main_vm.lua:202-207, raid_main_view.lua:299-304; 6543-6545 = {3,5}, raid 13001 = {1,20});
    //          else TeamTargetTable.MemberCountStopMatch; else PlayType == Raid(18); else the team type. Raid = cap > 5.
    //   affixes: dungeon_data:GetDungeonAffixDic(id).affixes + the key item's useItem.affixData.affixIds (:306-315),
    //          names/icons from AffixTableMgr (.Name / .Icon, common_tips_vm.lua:15-17).
    //   art (first non-empty): ChallengeHeroDungeonTable.Background, RaidDungeonTable.MonsterShowTexture (both keyed by
    //          DungeonId), TeamTargetTable.MatchPic via team_main GetTargetIdByDungeonId, MatchActTable.MatchPic where
    //          DungeonsId == id (:235-249), then the view's own default (:254). A raid's MonsterShowTexture is a
    //          TRANSPARENT cut-out: the raid menu draws it (rimg_monster) over DiffBgTexture (rimg_bg,
    //          raid_main_view.lua:315-317), so artBg carries that backdrop and C# composes the two (.ArtCompose.cs).
    //          Every lookup in its own pcall: the
    //          target lookup can throw (Dungeon-Confirm-Panel-Empty.md). GetRow(id, true) = no logError on a miss.
    //   difficulty (the header chip): label + numeric kind (1 green Normal/Easy, 2 amber Hard, 3 red Master,
    //          4 purple Nightmare). ⚠️ NEVER the raw DungeonsTable.DungeonTypeName: 527 rows hold a placeholder sentence
    //          ("He crouched down and started digging…"). Instead, by DungeonsTable.PlayType (EDungeonPlayType):
    //          MasterMode 17 → hero_dungeon_main GetHeroDungeonTypeName(id, diff) (hero_dungeon_main_vm.lua:869 =
    //          MasterChallengeDungeonTable.DungeonTypeName, all 380 rows a clean "Master N"; team_enter_view.lua:209-211
    //          does the same); HeroChallenge 9 → DungeonTypeName ("Normal"/"Hard", clean on all 40 rows) ONLY when
    //          ChallengeHeroDungeonTable.StarLevel is 1/2 (the hero menu's own tab key, hero_dungeon_main_view.lua:524);
    //          Raid 18 → RaidDungeonTable.Difficult 1/2/3 (raid_main_view.lua:87). The raid menu draws its difficulty
    //          as images (img_on_easy/difficulty/nightmare), no Lang key, so Easy/Hard/Nightmare are English literals.
    //          Anything else → no chip.
    //   ability score ("Ability Score" = fight point): team members' socialData.userAttrData.fightPoint (the
    //          proto UserAttrData.fight_point, stru_user_attr_data.proto:9; read by match_team_vm.lua:62 on the same
    //          team_data.TeamInfo.members objects GetTeamMemData returns, team_vm.lua:780-801); me =
    //          CharSerialize.fightPoint.totalFightPoint (recommend_fightvalue_vm.lua:20, live). Match players carry NO
    //          score (MatchPlayerShowInfo = name/gender/body/level/avatar/zone only, stru_match_player_show_info.proto:9)
    //          → only me + players also in my team get one; bots/AI and the rest send 0 → "—".
    //   Illusion-Breaking Strength (IBS, shown bracketed after the score): team members' socialData.userAttrData
    //          .seasonStrength (same UserAttrData object as fightPoint); me = PlayerEnt:GetLuaAttr(AttrSeasonStrength=11440)
    //          .Value (both verified live 2026-11-09). Same coverage as the score: match strangers / bots → 0 → no brackets.
    //   audio: the native view plays that row's MatchAudio once per open (:231/:251/:100). We DON'T — our own cue
    //          (embedded WAVs via winmm, .Sound.cs) replaces it, so it is heard even with the game muted / unfocused.
    private const string DcPollChunk = DcVoteHook +
@"pcall(function()
  local m = Z.UIMgr
  local st = m and m.__stellarDC
  rawset(_G, '__stellarDCSnap', nil)
  if not st or not m.__stellarDCInstalled then rawset(_G, '__stellarDCHead', 'X') return end
  local refuser, endKind, q, tt, aa = '', '', '', {}, {}
  pcall(function()
    local vs = st.votes and st.votes[st.seq]
    if vs then for cid, l in pairs(vs) do
      if l == 'R' then refuser = tostring(cid) endKind = 'R'
      elseif (l == 'C' or l == 'T') and endKind == '' then endKind = l end
      if l == 'T' then tt[#tt + 1] = tostring(cid) end
      if l == 'A' then aa[#aa + 1] = tostring(cid) end
    end end
    if refuser == '' then
      local r = st.closeRefuse and st.closeRefuse[st.seq]
      if (r == nil or r == 0) and not st.open and st.viewData then r = st.viewData.refuseId end
      if r and r ~= 0 then refuser = tostring(r) end
    end
    if st.voteQ and #st.voteQ > 0 then q = table.concat(st.voteQ, ';') st.voteQ = {} end
  end)
  rawset(_G, '__stellarDCHead', tostring(st) .. '|' .. tostring(st.seq) .. '|' .. (st.open and '1' or '0') .. '|' ..
    (st.native and '1' or '0') .. '|' .. refuser .. '|' .. endKind .. '|' .. q .. '|' .. table.concat(tt, ',') .. '|' .. table.concat(aa, ','))
  if not st.open or st.native then return end
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
    local function myFp()
      local okF, v = pcall(function() return Z.ContainerMgr.CharSerialize.fightPoint.totalFightPoint end)
      return okF and v or 0
    end
    local function memFp(mem)
      local okF, v = pcall(function() return mem.socialData.userAttrData.fightPoint end)
      return okF and v or 0
    end
    local function myIbs()
      local okI, v = pcall(function() return Z.EntityMgr.PlayerEnt:GetLuaAttr(Z.PbAttrEnum('AttrSeasonStrength')).Value end)
      return okI and tonumber(v) or 0
    end
    local function memIbs(mem)
      local okI, v = pcall(function() return mem.socialData.userAttrData.seasonStrength end)
      return okI and tonumber(v) or 0
    end
    local TM = Z.TableMgr
    local vd = st.viewData or {}
    local match = st.isMatching
    local myId = Z.ContainerMgr.CharSerialize.charBase.charId
    local dungeonId, diff, leader, twenty, total, targetRow = 0, 0, false, false, 30, nil
    local td, bi
    if match then
      local mtd = Z.DataMgr.Get('match_team_data')
      dungeonId = mtd:GetCurMatchingDungeonId() or 0
      diff = mtd:GetCurMatchingMasterDifficulty() or 0
      local tid = mtd:GetCurMatchingTargetId()
      targetRow = tid and TM.GetTable('TeamTargetTableMgr').GetRow(tid, true)
      if targetRow then
        twenty = not (targetRow.MemberCountStopMatch <= 5)
        local mr = TM.GetTable('MatchTableMgr').GetRow(targetRow.MatchID, true)
        if mr and mr.ConfirmTime then total = mr.ConfirmTime end
      end
    else
      local asp = vd.assignSceneParams
      dungeonId = asp and asp.sceneId or 0
      diff = asp and asp.initParam and asp.initParam.masterModeDiff or 0
      td = Z.DataMgr.Get('team_data')
      bi = td.TeamInfo.baseInfo
      leader = bi.leaderId == myId
      twenty = bi.teamMemberType == E.ETeamMemberType.Twenty
      total = Z.Global.TeamApplyActivityLastTime or 30
    end
    if not st.info then
      local info = { title = '', typeName = '', diffKind = 0, art = '', artBg = '', key = -1, affixes = {} }
      local cfg = TM.GetTable('DungeonsTableMgr').GetRow(dungeonId, true)
      if cfg then
        info.title = cfg.Name or ''
        local pt = cfg.PlayType
        if pt == E.DungeonType.MasterMode then
          if diff and diff > 0 then pcall(function()
            local n = Z.VMMgr.GetVM('hero_dungeon_main').GetHeroDungeonTypeName(dungeonId, diff)
            if n and n ~= '' then info.typeName = n info.diffKind = 3 end
          end) end
        elseif pt == E.DungeonType.HeroChallenge then pcall(function()
          local star = TM.GetTable('ChallengeHeroDungeonTableMgr').GetRow(dungeonId, true).StarLevel
          if star == 1 or star == 2 then info.typeName = cfg.DungeonTypeName or '' info.diffKind = star end
        end)
        elseif pt == E.DungeonType.Raid then pcall(function()
          local d = TM.GetTable('RaidDungeonTableMgr').GetRow(dungeonId, true).Difficult
          if d == 1 then info.typeName = 'Easy' info.diffKind = 1
          elseif d == 2 then info.typeName = 'Hard' info.diffKind = 2
          elseif d == 3 then info.typeName = 'Nightmare' info.diffKind = 4 end
        end) end
        if info.diffKind == 0 or info.typeName == '' then info.typeName = '' info.diffKind = 0 end
      end
      local function art(f) if info.art ~= '' then return end local okA, p = pcall(f) if okA and type(p) == 'string' and p ~= '' then info.art = p end end
      art(function() return TM.GetTable('ChallengeHeroDungeonTableMgr').GetRow(dungeonId, true).Background end)
      art(function() return TM.GetTable('RaidDungeonTableMgr').GetRow(dungeonId, true).MonsterShowTexture end)
      pcall(function()
        local r = TM.GetTable('RaidDungeonTableMgr').GetRow(dungeonId, true)
        if r and info.art == r.MonsterShowTexture and type(r.DiffBgTexture) == 'string' then info.artBg = r.DiffBgTexture end
      end)
      local audioRow = targetRow
      pcall(function()
        if not audioRow then
          local tid = Z.VMMgr.GetVM('team_main').GetTargetIdByDungeonId(dungeonId)
          audioRow = tid and TM.GetTable('TeamTargetTableMgr').GetRow(tid, true)
        end
      end)
      art(function() return audioRow.MatchPic end)
      -- caption only: is the DUNGEON a raid (cap > 5)? A Twenty team can run a 5-player dungeon -> 'PARTY'
      local cap = 0
      pcall(function() cap = tonumber(cfg.LimitedNum[2]) or 0 end)
      if cap <= 0 then pcall(function() cap = tonumber(audioRow.MemberCountStopMatch) or 0 end) end
      if cap > 0 then info.raidContent = cap > 5
      elseif cfg then info.raidContent = cfg.PlayType == E.DungeonType.Raid
      else info.raidContent = twenty end
      info.cap = cap
      if not audioRow then pcall(function()
        for _, row in pairs(TM.GetTable('MatchActTableMgr').GetDatas()) do
          if row.DungeonsId == dungeonId then audioRow = row break end
        end
      end) end
      art(function() return audioRow.MatchPic end)
      if info.art == '' then info.art = 'ui/background/seasonact/activity_dina_7' end
      local seen = {}
      local function addAffixes(list)
        if not list then return end
        for _, a in ipairs(list) do if not seen[a] then seen[a] = true info.affixes[#info.affixes + 1] = a end end
      end
      pcall(function() local dic = Z.DataMgr.Get('dungeon_data'):GetDungeonAffixDic(dungeonId) if dic then addAffixes(dic.affixes) end end)
      if not match then pcall(function()
        local ui = vd.dungeonInfo and vd.dungeonInfo.useItem
        if ui and ui.uuid > 0 then
          addAffixes(ui.affixData.affixIds)
          local lim = Z.CounterHelper.GetCounterLimitCount(Z.Global.KeyRewardLimitId)
          info.key = lim - vd.awardCountInfo[bi.leaderId].keyAwardCount
        end
      end) end
      st.info = info
    end
    local info = st.info
    rec('H', match and 'M' or 'T', dungeonId, b(twenty), total, b(leader), b(st.doing), info.title, info.typeName, info.art, info.key, info.diffKind, info.artBg, info.cap, b(info.raidContent))
    local AT = TM.GetTable('AffixTableMgr')
    for _, a in ipairs(info.affixes) do
      local row = AT.GetRow(a, true)
      if row then rec('A', a, row.Name, row.Icon) end
    end
    if match then
      local md = Z.DataMgr.Get('match_data')
      local teamFp, teamIbs = {}, {}
      pcall(function()
        for _, mem in pairs(Z.DataMgr.Get('team_data').TeamInfo.members or {}) do
          if not mem.isAi then teamFp[mem.charId] = memFp(mem) teamIbs[mem.charId] = memIbs(mem) end
        end
      end)
      for _, mi in pairs(md.matchPlayerInfo_ or {}) do
        local name, lvl, prof = '', 0, mi.professionId or 0
        local show = mi.matchShowInfo
        if mi.isBot then
          lvl = show and show.level or 0
          local bot = TM.GetRow('BotAITableMgr', mi.charId)
          if bot then name = bot.Name prof = bot.Weapon[1] end
        elseif show then name = show.name lvl = show.level end
        local r = (mi.readyStatus == E.RedayType.Ready) and 1 or ((mi.readyStatus == E.RedayType.UnReady) and 2 or 0)
        local fp, ibs = 0, 0
        if not mi.isBot then
          if mi.charId == myId then fp = myFp() ibs = myIbs() else fp = teamFp[mi.charId] or 0 ibs = teamIbs[mi.charId] or 0 end
        end
        rec('M', mi.charId, name, lvl, prof, b(mi.isBot), b((not mi.isBot) and mi.isAssist), r, 0, 0, b(mi.charId == myId), fp, ibs)
      end
    else
      local agree = {}
      for _, c in ipairs(vd.agreeMem or {}) do agree[c] = true end
      local list = Z.VMMgr.GetVM('team').GetTeamMemData()
      for _, mem in ipairs(list) do
        local cid = mem.charId
        local sd = mem.socialData
        local name, lvl, prof = '', 0, 0
        if mem.isAi then
          lvl = td:GetLeaderLevel()
          local bot = sd and sd.basicData and TM.GetRow('BotAITableMgr', sd.basicData.botAiId)
          if bot then name = bot.Name prof = bot.Weapon[1] end
        elseif sd and sd.basicData then
          name = sd.basicData.name lvl = sd.basicData.level
          if sd.professionData then prof = sd.professionData.professionId end
        end
        local assist = false
        if not mem.isAi then pcall(function() assist = vd.awardCountInfo[cid].isAssist and true or false end) end
        local isLeader = cid == bi.leaderId
        local r = (isLeader or agree[cid]) and 1 or 0
        local v = st.readyMap[cid]
        if v == true then r = 1 elseif v == false then r = 2 end
        local slot = 0
        if twenty then pcall(function() slot = Z.VMMgr.GetVM('team').GetTeamTwentyIndex(cid) or 0 end) end
        local fp, ibs = 0, 0
        if not mem.isAi then
          fp = memFp(mem) ibs = memIbs(mem)
          if cid == myId then
            local v = myFp() if v and v > 0 then fp = v end
            local w = myIbs() if w and w > 0 then ibs = w end
          end
        end
        rec('M', cid, name, lvl, prof, b(mem.isAi), b(assist), r, slot, b(isLeader), b(cid == myId), fp, ibs)
      end
    end
    return table.concat(out, RS)
  end)
  rawset(_G, '__stellarDCSnap', ok and res or ('ERR ' .. tostring(res)))
end)";

    // The poll above is ~200 lines of Lua. Sending it through DoString every 200 ms meant marshalling + COMPILING it 5×/s
    // whether or not a request was up — the ~1 ms 'stellar.dungeonhub' that showed on every [Hitch] line of the
    // 2026-11-10 raid log. It is now compiled ONCE into a global function and each tick only runs a one-line call.
    // The call clears the head first, so a missing function (first tick, or a fresh Lua state after a relog) reads as
    // null → define it and call again. Bump the name's suffix whenever DcPollChunk changes, so a function left in _G by
    // an older build can never answer for the new one.
    private const string DcPollFn = "__stellarDCPollFn1";
    private const string DcPollCall =
        "rawset(_G, '__stellarDCHead', nil) local f = rawget(_G, '" + DcPollFn + "') if f then f() end";
    private const string DcPollDefine = "rawset(_G, '" + DcPollFn + "', function()\n" + DcPollChunk + "\nend)";

    // Runs the poll; returns the parked head (null = Lua not answering), as DoString(DcPollChunk) + the head read did.
    private string? DcPoll()
    {
        _services.Lua.DoString(DcPollCall);
        var head = _services.Lua.ReadGlobalString("__stellarDCHead");
        if (head != null) return head;
        _services.Lua.DoString(DcPollDefine);
        _services.Lua.DoString(DcPollCall);
        return _services.Lua.ReadGlobalString("__stellarDCHead");
    }

    // Seq-guarded Lua helpers: each acts only while the stash is still the request the window is showing.
    private static string DcGuard(string body, int seq) =>
        "pcall(function() local m = Z.UIMgr local st = m and m.__stellarDC" +
        " if not st or st.seq ~= " + seq + " then return end " + body + " end)";

    // Safety net: open the game's OWN view with the stashed args (C#-driven; the original OpenView is kept on Z.UIMgr).
    private void DcOpenNative(int seq) => _services.Lua.DoString(DcGuard(
        "if st.open and not st.native then st.native = true m.__stellarDCOrigOpenView(m, 'team_enter', st.viewData) end", seq));

    // Our own close (local countdown ran out, or we're dropping a request after a scene change): mark the stash closed.
    private void DcCloseStash(int seq) => _services.Lua.DoString(DcGuard("st.open = false", seq));

    // ── actions ──────────────────────────────────────────────────────────────────────────────────────────────────
    // Same calls as team_enter_view's buttons (setViewInfo :263-292 / setMatchViewInfo :170-194, verified against
    // team_enter_vm.lua:31-45 and match_vm.lua:96-103), each in a coroutine (they're RPCs) with a fresh token from
    // team_data.CancelSource (team_enter_vm.lua:57 HandleTeamActivity does the same). CheckIsInTeam() false → the view
    // just closes, like the native buttons.
    private const string DcTeamPrologue =
        " local vm = Z.VMMgr.GetVM('team_enter')" +
        " if not Z.VMMgr.GetVM('team').CheckIsInTeam() then vm.CloseEnterView() return end" +
        " local tok = Z.DataMgr.Get('team_data').CancelSource:CreateToken()";

    private void DcLuaAction(int seq, string coroBody) => _services.Lua.DoString(DcGuard(
        "if not st.open then return end Z.CoroUtil.create_coro_xpcall(function()" + coroBody + " end)()", seq));

    private void DcLuaReply(int seq, bool agree) => DcLuaAction(seq, DcTeamPrologue +
        " vm.AsyncReplyJoinActivity(" + (agree ? "true" : "false") + ", tok)" + (agree ? "" : " vm.CloseEnterView()"));

    private void DcLuaLeaderCancel(int seq) => DcLuaAction(seq, DcTeamPrologue +
        " vm.AsyncTeamCancelActivity(tok) vm.CloseEnterView()");

    private void DcLuaMatchReady(int seq, bool ready) => DcLuaAction(seq,
        " Z.VMMgr.GetVM('match').AsyncMatchReady(" + (ready ? "true" : "false") + ")");
}
