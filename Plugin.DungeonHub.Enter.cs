using System;
using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeons hub — Solo / Party ENTER directly: the same call the game page's own Enter button makes, with the same
// values the page would compute and every check it does first. Recipe + citations: Knowledge Base\Dungeon-Access-Hub.md
// §4 "Enter recipe" (Resources\release_3.7\lua\lua line refs below).
//
// Both pages end in ui_enterdungeonscene AsyncCreateLevel(playType, dungeonId, token, affix, roomId, selectType,
// keyUuid, masterDiff) (ui_enterdungeonscene_vm.lua:241-257: Condition check with the game's tip → StartEnterDungeon
// RPC → ShowTips(ret) → return ret). The page VMs' wrappers (hero_dungeon_main_vm.lua:157 / raid_vm.lua:71) pass the
// same args but DROP ret, so we call AsyncCreateLevel ourselves to log the server's code. The only thing skipped is the
// hero wrapper's post-entry "no rewards left" tip (130011/130012), which reads page-computed state (IsHaveAward).
//
//   hero (hero_dungeon_main_view.lua:209-276 enterBtn_, :782-816 refreshShowDungeon)
//     playType = EFunctionType.FunctionTypeHeroDungeonChallenge (hero_dungeon_main_data.FunctionId as the NPC sets it)
//     dungeonId = GetHeroDungeonGroup(group)[level].DungeonId; Solo on Normal/Hard enters DungeonsTable.SingleModeDungeonId
//     Solo  : selectType 1 when that row's SingleAiMode == 1 (Master: MasterChallengeDungeonTable.SingleAiMode == 1), else 2;
//             refused (tip 3333) when the party is bigger than SingleModeLimitNum[2] (default 5); keyUuid 0
//     Party : selectType 2; on a level that HAS solo the page wants LimitedNum[1] members (default 5) and otherwise
//             opens a "go match instead" dialog — we refuse with the reason instead of running that dialog
//     affix nil, roomId nil, masterDiff = the Master level (0 on Normal/Hard)
//   Unstable (hero_dungeon_instability_main_view.lua:128-178 enterBtn_, hero_dungeon_main_vm.lua:157-159)
//     playType = EFunctionType.FunctionTypeHeroDungeonNormal (300201); dungeonId = the TEAM id even for solo (the page
//     sends its dungeonId_; the solo id only drives its labels); affix = hero_dungeon_main.GetAffix(teamId); keyUuid 0
//     Solo  : selectType 1 (SingleAiMode == 1); party bigger than the solo row's SingleModeLimitNum[2] → tip 3333
//     Party : selectType 2; fewer than LimitedNum[1] (3) → the page opens a "match instead" dialog — refused instead
//   raid (raid_main_view.lua:43-66 btn_go)
//     playType = DungeonsTable.FunctionID, affix = hero_dungeon_main.GetAffix(id), selectType 1 (Solo, SingleAiMode == 1)
//     or 2; Party outside a team → the page's own team-target picker (team_target.OpenTeamTargetView), nothing sent
// Results parked in __stellarDHEnter: 'SENT <args>' · 'NO <reason>' (refused, nothing sent) · 'DIALOG <what>' (the
// game's own helper opened instead) · 'PAGE <why>' / 'ERR …' (unexpected data → fall back to opening the page).
// The RPC's return code arrives later in __stellarDHEnterRet (written by the coroutine; read on the hub tick).
public sealed partial class Plugin
{
    private const double DhEnterWaitSec = 15.0, DhNoticeSec = 8.0;

    private bool   _dhEnterWait;
    private double _dhEnterSince;
    private string _dhEnterWhat = "";

    // The reason a press was refused, shown in the bottom bar's left text for DhNoticeSec.
    private string _dhNotice = "";
    private double _dhNoticeUntil;
    private bool DhNoticeOn => _dhNotice.Length > 0 && DcNow < _dhNoticeUntil;
    private string DhIdleText() => DhNoticeOn ? _dhNotice : L("dh.bar.notMatching");
    private ColorRgba DhIdleColor() => DhNoticeOn ? DhAmber : MutedColor();
    private void DhSetNotice(string text) { _dhNotice = text; _dhNoticeUntil = DcNow + DhNoticeSec; }

    private void DhOnEnter(bool solo)
    {
        var e = DhCur; var lv = DhCurLevel;
        if (e == null || lv == null || !DhBtnState(solo ? 0 : 2).Enabled) return;
        if (!_services.Lua.Ready) { _services.Log.Warning("[DungeonHub] enter: Lua not ready"); return; }
        string mode = solo ? "solo" : "party";
        string what; string chunk;
        if (e.Tab == DhTabRaid)
        {
            what = $"enter raid group={e.Group} dungeon={lv.DungeonId} mode={mode} ({e.Name} {lv.Label})";
            chunk = DhEnterRaidChunk(lv.DungeonId, solo);
        }
        else if (DhIsUnstable)
        {
            what = $"enter unstable dungeon={lv.DungeonId} mode={mode} ({e.Name})";
            chunk = DhEnterUnstableChunk(lv.DungeonId, solo);
        }
        else
        {
            int level = _dhLevelIdx, md = DhIsMaster ? _dhMasterDiff : 0;   // chip 1 Normal / 2 Hard / 3 Master = StarLevel
            what = $"enter hero group={e.Group} level={level} master={md} mode={mode} ({e.Name})";
            chunk = DhEnterHeroChunk(e.Group, level, md, solo);
        }
        _services.Lua.DoString(chunk);
        var res = _services.Lua.ReadGlobalString("__stellarDHEnter") ?? "ERR (no result)";
        if (res.StartsWith("SENT", StringComparison.Ordinal))
        {
            _services.Log.Info($"[DungeonHub] {what}: sent AsyncCreateLevel({res.Substring(4).Trim()})");
            _dhEnterWhat = what; _dhEnterSince = DcNow; _dhEnterWait = true; _dhEnterGh = false; _dhEnterCm = false;
            _dhNotice = "";
            DhHide();   // close-on-action, as before: Party → the team_enter vote (our Dungeon Confirm), Solo → loading
            DhFollowEnter();   // a refusal before the RPC (Condition) returns synchronously
        }
        else if (res.StartsWith("NO", StringComparison.Ordinal))
        {
            string why = res.Substring(2).Trim();
            _services.Log.Info($"[DungeonHub] {what}: not sent — {why}");
            DhSetNotice(LF(solo ? "dh.notice.solo" : "dh.notice.party", DhReason(why)));
        }
        else if (res.StartsWith("DIALOG", StringComparison.Ordinal))
        {
            _services.Log.Info($"[DungeonHub] {what}: not sent — {res.Substring(6).Trim()}");
            DhHide();   // the game's own window is up; the hub would cover it
        }
        else
        {
            // PAGE / ERR: something the recipe didn't expect — let the page decide, as before this change.
            _services.Log.Warning($"[DungeonHub] {what}: {res} — falling back to opening the page");
            DhOnPage(solo);
        }
    }

    // Hub tick (also while hidden): the coroutine's AsyncCreateLevel return code, or a timeout.
    private void DhFollowEnter()
    {
        if (!_dhEnterWait || !_services.Lua.Ready) return;
        var ret = _services.Lua.ReadGlobalString("__stellarDHEnterRet");
        if (ret != null)
        {
            _dhEnterWait = false;
            // 0 = accepted. nil = the client Condition check refused (the game showed its tip). Other = server error
            // code, already shown by AsyncCreateLevel's ShowTips(ret).
            string meaning = ret == "0" ? "ok" : ret == "nil" ? "refused by the entry Condition (game tip shown)" : "server error code (game tip shown)";
            _services.Log.Info($"[DungeonHub] {_dhEnterWhat}: result ret={ret} — {meaning}");
            if (_dhEnterGh) DhGhAfterResult(ret);   // Guild Hunt: a server code → the hunt-NPC fallback (.GuildHunt.cs)
            if (_dhEnterCm) DhCmAfterResult(ret);   // Bureau Commissions: a server code → Teleport (.Commissions.cs)
            return;
        }
        if (DcNow - _dhEnterSince < DhEnterWaitSec) return;
        _dhEnterWait = false; _dhEnterCm = false;
        _services.Log.Warning($"[DungeonHub] {_dhEnterWhat}: no reply within {DhEnterWaitSec:0} s (a Lua error in the coroutine lands in the game log)");
    }

    private static string DhEnterHeroChunk(int group, int level, int diff, bool solo) => DhEnterWrap(
        " local SOLO, LEVEL, DIFF = " + Q(solo) + ", " + Q(level) + ", " + Q(diff) +
        " local DT = Z.TableMgr.GetTable('DungeonsTableMgr')" +
        " local hvm = Z.VMMgr.GetVM('hero_dungeon_main')" +
        " local grp = Z.VMMgr.GetVM('dungeon').GetHeroDungeonGroup(" + Q(group) + ")" +
        " local ent = grp and grp[LEVEL]" +
        " if not ent or not grp[1] then res = 'PAGE no level ' .. LEVEL .. ' in group' return end" +
        " local id = ent.DungeonId" +
        " local row = DT.GetRow(id, true)" +
        " if not row then res = 'PAGE no DungeonsTable row ' .. tostring(id) return end" +
        // the page only exists behind GoToFunc(FunctionID) → CheckFuncCanUse (switch / scene / buff / dead, with tips)
        " if not Z.VMMgr.GetVM('gotofunc').CheckFuncCanUse(DT.GetRow(grp[1].DungeonId).FunctionID, false) then res = 'NO not allowed here right now' return end" +
        " local isMaster = LEVEL == 3 local mcfg" +
        " if isMaster then" +
        "   local mm = require('table.MasterChallenDungeonTableMap').DungeonId[id]" +
        "   mcfg = mm and mm[DIFF] and Z.TableMgr.GetTable('MasterChallengeDungeonTableMgr').GetRow(mm[DIFF], true)" +
        "   if not mcfg then res = 'PAGE no Master row for level ' .. DIFF return end" +
        "   if DIFF > (hvm.GetMasterDungeonMaxDiff(id) or 0) + 1 then res = 'NO this Master level is locked' return end" +
        " elseif not hvm.IsUnlockDungeonId(id) then res = 'NO this difficulty is locked' return end" +
        DhEnterTeamLua +
        " local hasSingle if isMaster then hasSingle = mcfg.SingleAiMode == 1 else hasSingle = (row.SingleModeDungeonId or 0) ~= 0 end" +
        " local sel, enterId, key = 2, id, nil" +
        " if SOLO then" +
        "   if not hasSingle then res = 'NO no solo on this level' return end" +
        "   local cond if isMaster then cond = mcfg.SingleAiCondition else cond = row.SingleAiCondition end" +
        "   if not Z.ConditionHelper.CheckCondition(cond, true) then res = 'NO solo not unlocked yet' return end" +
        "   if not isMaster then enterId = row.SingleModeDungeonId end" +
        "   local erow = DT.GetRow(enterId, true)" +
        "   if not erow then res = 'PAGE no DungeonsTable row for solo id ' .. tostring(enterId) return end" +
        "   local src if isMaster then src = mcfg else src = erow end" +
        "   local maxC = lim(src.SingleModeLimitNum, 2) or 5" +
        "   if maxC < count then Z.TipsVM.ShowTips(3333) res = 'NO party too big for solo (' .. count .. ' > ' .. maxC .. ')' return end" +
        "   key = 0" +
        "   if erow.SingleAiMode == 1 then sel = 1 end" +
        "   if isMaster and mcfg.SingleAiMode == 1 then sel = 1 end" +
        " elseif hasSingle then" +
        "   key = 0" +
        "   local minC = lim(row.LimitedNum, 1) or 5" +
        "   if count < minC then res = 'NO needs ' .. minC .. '+ in the party (you have ' .. count .. ') - use Match' return end" +
        " end" +
        " local pt = Z.PbEnum('EFunctionType', 'FunctionTypeHeroDungeonChallenge')" +
        " local md = 0 if isMaster then md = DIFF end" +
        // leave the game's hero data exactly as the NPC + page would (anything reading it after entry sees the same)
        " local hd = Z.DataMgr.Get('hero_dungeon_main_data') hd:SetChallengeScenceId(grp) hd:SetFunctionId(pt)" +
        " send(pt, enterId, nil, sel, key, md)");

    private static string DhEnterRaidChunk(int dungeonId, bool solo) => DhEnterWrap(
        " local SOLO, id = " + Q(solo) + ", " + Q(dungeonId) +
        " local row = Z.TableMgr.GetTable('DungeonsTableMgr').GetRow(id, true)" +
        " local rr = Z.TableMgr.GetTable('RaidDungeonTableMgr').GetRow(id, true)" +
        " if not row or not rr then res = 'PAGE no raid row ' .. id return end" +
        " if not Z.VMMgr.GetVM('raid'):CheckAnyDiffRaidDungeonOpen(rr.GroupId, true) then res = 'NO this raid is closed' return end" +
        // raid_main_view.lua:380 checkDungeonAnyBossOpen: CheckFuncCanUse + one boss in its OpenTimerId
        " if not Z.VMMgr.GetVM('gotofunc').CheckFuncCanUse(row.FunctionID, false) then res = 'NO not allowed here right now' return end" +
        " local open = false" +
        " for _, b in ipairs(rr.BossId) do local br = Z.TableMgr.GetTable('RaidBossTableMgr').GetRow(b, true)" +
        "   if br and Z.TimeTools.CheckIsInTimeByTimeId(br.OpenTimerId) then open = true break end end" +
        " if not open then res = 'NO no boss open on this difficulty' return end" +
        DhEnterTeamLua +
        " local sel = 2" +
        " if SOLO and row.SingleAiMode == 1 then" +
        "   if not Z.ConditionHelper.CheckCondition(row.SingleAiCondition, true) then res = 'NO solo not unlocked yet' return end" +
        "   sel = 1" +
        " elseif not inTeam then" +
        "   local tid = nil pcall(function() tid = Z.VMMgr.GetVM('team_main').GetTargetIdByDungeonId(id) end)" +
        "   Z.VMMgr.GetVM('team_target').OpenTeamTargetView({ targetId = tid or E.TeamTargetId.Costume })" +
        "   res = 'DIALOG not in a party - opened the game team-target picker (target ' .. tostring(tid) .. ')' return" +
        " end" +
        " send(row.FunctionID, id, Z.VMMgr.GetVM('hero_dungeon_main').GetAffix(id), sel, nil, nil)");

    // Unstable: the page's enterBtn_ (hero_dungeon_instability_main_view.lua:128-178). Its solo arm checks the SHOWN
    // row (the solo id, getShowDungeonData :635) for SingleAiCondition and SingleModeLimitNum, and still sends the team
    // id; the hero data is left as its NPC sets it (SetScenceId(id) + FunctionId 300201).
    private static string DhEnterUnstableChunk(int dungeonId, bool solo) => DhEnterWrap(
        " local SOLO, id = " + Q(solo) + ", " + Q(dungeonId) +
        " local DT = Z.TableMgr.GetTable('DungeonsTableMgr')" +
        " local row = DT.GetRow(id, true)" +
        " if not row or not Z.TableMgr.GetTable('NormalHeroDungeonTableMgr').GetRow(id, true) then res = 'PAGE no Unstable row ' .. id return end" +
        " if not Z.VMMgr.GetVM('gotofunc').CheckFuncCanUse(row.FunctionID, false) then res = 'NO not allowed here right now' return end" +
        DhEnterTeamLua +
        " local sel = 2" +
        " if SOLO then" +
        "   if row.SingleAiMode ~= 1 then res = 'NO no solo on this dungeon' return end" +
        "   local srow = DT.GetRow(row.SingleModeDungeonId or 0, true) or row" +
        "   if not Z.ConditionHelper.CheckCondition(srow.SingleAiCondition, true) then res = 'NO solo not unlocked yet' return end" +
        "   local maxC = lim(srow.SingleModeLimitNum, 2) or 5" +
        "   if maxC < count then Z.TipsVM.ShowTips(3333) res = 'NO party too big for solo (' .. count .. ' > ' .. maxC .. ')' return end" +
        "   sel = 1" +
        " else" +
        "   local minC = lim(row.LimitedNum, 1) or 1" +
        "   if count < minC then res = 'NO needs ' .. minC .. '+ in the party (you have ' .. count .. ') - use Match' return end" +
        " end" +
        " local pt = Z.PbEnum('EFunctionType', 'FunctionTypeHeroDungeonNormal')" +
        " local hd = Z.DataMgr.Get('hero_dungeon_main_data') hd:SetScenceId(id) hd:SetFunctionId(pt)" +
        " send(pt, id, Z.VMMgr.GetVM('hero_dungeon_main').GetAffix(id), sel, 0, nil)");

    // Party role + size, shared: a member can't start anything (the server would refuse; the hub disables it too).
    private const string DhEnterTeamLua =
        " local tv = Z.VMMgr.GetVM('team')" +
        " local inTeam = tv.CheckIsInTeam()" +
        " if inTeam and not tv.GetYouIsLeader() then res = 'NO leader only' return end" +
        " local count = 0 pcall(function() count = table.zcount(Z.DataMgr.Get('team_data').TeamInfo.members) end)";

    // send(): the coroutine around AsyncCreateLevel (an RPC — must yield), parking its return code. No pcall inside
    // the coroutine (yield across pcall is unsafe on this VM); a Lua error goes to create_coro_xpcall's handler and C#
    // times out instead. lim(arr, i) = a table IntArray element, nil when absent (empty arrays read as nil).
    private static string DhEnterWrap(string body) =>
        "pcall(function() rawset(_G, '__stellarDHEnter', nil) rawset(_G, '__stellarDHEnterRet', nil) local res = 'ERR no result'" +
        " local function lim(a, i) local v = nil pcall(function() v = tonumber(a[i]) end) if v and v > 0 then return v end return nil end" +
        " local function list(t) if type(t) ~= 'table' then return tostring(t) end local o = {} for _, v in ipairs(t) do o[#o + 1] = tostring(v) end return '{' .. table.concat(o, ',') .. '}' end" +
        " local function send(pt, id, affix, sel, key, md)" +
        "   local vm = Z.VMMgr.GetVM('ui_enterdungeonscene')" +
        "   local tok = Z.DataMgr.Get('team_data').CancelSource:CreateToken()" +
        "   res = 'SENT playType=' .. tostring(pt) .. ' dungeonId=' .. tostring(id) .. ' affix=' .. list(affix) .. ' roomId=nil selectType=' .. tostring(sel)" +
        "     .. ' keyUuid=' .. tostring(key) .. ' masterDiff=' .. tostring(md)" +
        "   Z.CoroUtil.create_coro_xpcall(function()" +
        "     local ret = vm.AsyncCreateLevel(pt, id, tok, affix, nil, sel, key, md)" +
        "     rawset(_G, '__stellarDHEnterRet', tostring(ret))" +
        "   end)()" +
        " end" +
        " local ok, err = pcall(function()" + body + " end)" +
        " rawset(_G, '__stellarDHEnter', ok and res or ('ERR ' .. tostring(err))) end)";
}
