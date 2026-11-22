using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — the NPC tab (2026-11-20, user-approved _scratch\prototypes\dungeon-hub-npc-tab-APPROVED.html): one list
// entry per service NPC, one Open per service, so an NPC's pages open from anywhere. Game facts: Dungeon-Access-Hub.md §13
// (static research only — the opens below were NOT tested in game when this was written).
//
//   NPC      NpcTable row → Name / Title / InteractionTemplate ({state, interactionId} pairs, in the NPC's own order).
//   service  three sources, in this order, a function id listed once (the first source wins):
//            1. the NPC row's `pre` ids below — pages the NPC teaches but has no button for (life-skill instructors:
//               the profession's crafting screen, FunctionTable Note 生活职业, user 2026-11-21);
//            2. each InteractiveTable row of the template whose ActionStage holds ['4', fid] (=
//               EInteractionActionFunctionEntry, whose handler is just gotofunc.GoToFunc(fid) with no NPC argument,
//               interaction_action.lua:24-30). Other interactions (talk 3002, …) are skipped;
//            3. NpcTable.NpcFunctionID = {interactBtnId, fid} pairs = the options INSIDE the talk dialog, opened with
//               CheckFuncCanUse + GoToFunc (talk_option_vm.lua:163-208). Maxim / Grace have services only here.
//            Name = FunctionTable[fid].Name; a life-skill MATERIAL shop (ParentId 800981 = E.FunctionID.LifeShop) has
//            the same name as its craft, so C# labels it "dh.npc.materialShop".
//   hidden   an event / time-limited service that is off right now is LEFT OUT (the game hides its button too), not
//            shown Locked — DhNpcHiddenLua. Ordinary locks (level / quest) still show as Locked.
//   locked   gotofunc.FuncIsOn(fid, true) = switch.CheckFuncSwitch(fid) — the unlock alone, no tip. NOT
//            CheckFuncCanUse(fid, true): that adds the scene / buff / dead checks and would show every row Locked while
//            standing in a dungeon (the raid list's reason for the same choice, §8 v4). The press re-checks it all.
//   open     gotofunc.GoToFunc(fid) — CheckFuncCanUse(fid, false) (the game's own tip on refusal) + FunctionOrder.
//            800527 Matrix Manifestation only: GoToFunc false WITHOUT a lock / scene reason → season_talent
//            OpenRefineView() (the Refine tab of season_talent_function_main, §13). Life-skill crafts: DhLifeCraftPro.
//
// Records (FULL poll only, after the activities): E (tab 4, key 'n<npcId>', name, sub = the NPC title, or our DhNpcTitle override —
//   also the list row's whole sub-line, user 2026-11-21: the old " · N services" tail got truncated) and per SHOWN service
//   S  key, slot (0-based, shown order), funcId, name, locked, material shop
//   N  key, banner, banner fallback, thumb, backdrop — the NPC's art paths (.NpcArt.cs); banner / fallback / backdrop
//      from the NPCS row below (backdrop: a '|'-joined chain), thumb = NpcTable.MapIcon, else the first service's
//      FunctionTable DialogIcon / Icon
// The NpcTable / InteractiveTable / FunctionTable walk runs ONCE per login (cached on the hub's Lua stash st.npcs, a new
// Z.UIMgr on relog starts clean); a full poll then only re-reads each service's lock and hidden state.
public sealed partial class Plugin
{
    private const int DhNpcSlots = 7;   // fixed service rows: nothing shifts between NPCs (Cassie has 7)
    private const int DhMatrixFunc = 800527;   // E.FunctionID.SeasonTalentFactorRefine (Matrix Manifestation)

    private sealed class DhService { public int FuncId; public string Name = ""; public bool Locked; }

    // ── geometry (mock px → ×1.6, DcS / DcSE only: see the static-order note in .Window.cs) ─────────────────────────
    // Rows 54 (64 while there were 6) so SEVEN fit the same block: 7 × 54 + 6 × 6 = 414 ≤ 436; the 44 px Open pill stays.
    private static readonly float DhSvcH = DcSE(34f), DhSvcGap = DcS(4f);                                  // 54 · 6
    private static readonly float DhSvcPadL = DcS(10f), DhSvcPadR = DcS(6f), DhSvcTagGap = DcS(6f);        // 16 · 10 · 10
    // The services block takes the rest of the panel column (art · name · title · SERVICES caption · block, 4 gaps), so
    // the panel is exactly as tall as every other tab's. A PROPERTY, not a static readonly: it reads .Window.cs fields,
    // whose initialisers may not have run yet when this file's do (partial-file static order is unspecified).
    private static float DhSvcBlockH => DhColsH - 2f * DhPanPadY - DhArtH - DhNameH - DhActSubH - DhCapH - 4f * DhPanGap;   // 436 ≥ 7 × 54 + 6 × 6

    // Life-skill crafting function → { LifeProfessionTable ProId, workbench camera id } (FunctionId 501004-501009 →
    // ProId 201-206). GoToFunc of these is unverified (their FunctionOrder is empty in the extracted tables), and
    // life_profession.OpenLifeProfessionMainView(proId) is WRONG — it lands on the general Life Skill page (user-tested
    // 2026-11-21). The crafting screen is what the WORKBENCH opens: InteractiveTable action 28 EInteractionActionCookFunc
    // → interaction_action.lua CookFunc (:170), dispatched in DhNpcOpenChunk. Camera = the bench's InteractiveTable param
    // (rows 2005/2050-2054). NOT nil: no view skips the camera on nil — each falls back to its DefaultCameraId
    // (cast_main_view :54-55 → 4000 = the CULINARY cam for every craft; cook_main_view :527/:595 → 4000, but getEntity
    // :504 tests camID ~= 4000 so nil picks the wrong bench entity 105; chemistry_main_view :30-32 → 4100).
    private const string DhLifeCraftPro = "{ [501004] = {201, 4000}, [501005] = {202, 4100}, [501006] = {203, 4110}," +
                                          " [501007] = {204, 4120}, [501008] = {205, 4130}, [501009] = {206, 4140} }";

    private const string DhNpcCatalogLua = @"
    -- NPC tab (tab 4, .Npc.cs).
    pcall(function()
      local FTn = TM.GetTable('FunctionTableMgr')
      -- Mael's banner emblem: the Matrix Manifestation page's art — Z.Global.SeasonTalentEntryStorageIcon = {{seasonId,
      -- path}, …}, the pair of the current season as season_talent_refine_sub_view.refreshTalentStorageIcon picks it (no
      -- match → the last pair, then the S4 literal).
      local function seasonArt()
        local sid = tostring(try(function() return Z.VMMgr.GetVM('season').GetCurrentSeasonId() end, ''))
        local hit, last
        pcall(function() for _, v in ipairs(Z.Global.SeasonTalentEntryStorageIcon) do
          last = v[2] if tostring(v[1]) == sid then hit = v[2] end
        end end)
        return hit or last or 'ui/textures/season_talent/season_talent_icon_s4'
      end
      -- NPCS = the listed service NPCs, in list order: ONE row per NPC, so listing another is a one-line change.
      --   [1] NpcTable id · e = banner emblem (a path, or a function returning one) · f = its fallback (e did not load)
      --   bg  = the banner's scenery backdrop (.NpcArt.cs; NPC IMAGE RULE, Dungeon-Access-Hub.md §13): one path, with AST
      --         appended as its fallback — a CHAIN tried in order, the first that loads wins. No bg = AST alone: every NPC below Mael stands in
      --         Asterleeds (scene 8), which has NO scenic-spot photo → Asterleeds-themed event art. ⚠️ NOT the town's
      --         environment picture ui/textures/env_textures/main_bg_4 (Global.lua:520): it does not ship (user log of
      --         70d0525: FAILED for every NPC). The chain is sent as one '|'-joined string, so C# resolves it ONCE for
      --         every NPC sharing it (.NpcArt.cs DhBgChainOf). Mael: the Lallipar Village panorama (MapStickerTable).
      --   pre = extra function ids shown FIRST (not on the NPC's buttons).
      -- Subtitles are C# catalog keys dh.npc.title.<id> (.Npc.cs DhNpcTitle); no key = the game's NpcTable.Title.
      local SA, TA = 'ui/background/seasonact/', 'ui/textures/themeact/themeact_bg/activity_themeact_'
      local AST = {
        SA .. 'activity_seasonact_201',   -- City Rally (Asterleeds)
        SA .. 'activity_seasonact_208',   -- Street Theater
        SA .. 'activity_seasonact_203',
        SA .. 'activity_fireworks',
        TA .. '331', TA .. '333', TA .. '325',
      }
      local TAB, SDT = 'ui/atlas/item/c_tab_icon/com_icon_tab_', 'ui/textures/sevendaystarget/mengxin_'
      local CP, LP = 'ui/textures/collection_profession/collection_profession_pic_', 'ui/atlas/life_profession/life_profession_'
      local FP = 'ui/textures/feature_preview/feature_preview_picture_20'
      local NPCS = {
        { 1008600, e = seasonArt, f = 'ui/atlas/season_talent/img_seasontalent_main_talent4',
          bg = 'ui/textures/scenicspot/pic_scenicspotreputation_cunzhuangquanjing' },   -- Mael 'Legacy Expert'
        { 106,      e = 'ui/textures/treasure/treasure_01' },                       -- Stella
        { 21701001, e = TAB .. '302' },                                             -- Milia 'Class Guide'
        { 1008889,  e = TAB .. '83' },                                              -- Erica
        { 1008754,  e = SDT .. 'skill02_1' },                                       -- Mason
        { 1008079,  e = SDT .. 'refine_1' },                                        -- Maxim
        { 1008801,  e = SDT .. 'equip01_1' },                                       -- Harvey
        { 1008078,  e = TAB .. '81' },                                              -- Grace
        { 1008599,  e = FP },                                                       -- Lunel
        { 1008000,  e = FP },                                                       -- Cassie
        { 1008214,  e = TAB .. '246' },                                             -- Cuthbert
        { 1008075,  e = TAB .. '209' },                                             -- Kalista
        { 21707005, e = CP .. '2',  f = LP .. 'refine',     pre = { 501005 } },     -- Maurice, Alchemy
        { 1008615,  e = CP .. '1',  f = LP .. 'magiccraft', pre = { 501007 } },     -- Hamilton, Gemcrafting
        { 1008646,  e = CP .. '6',  f = LP .. 'foundry',    pre = { 501006 } },     -- Gary, Smelting
        { 21707006, e = CP .. '10', f = LP .. 'woodwork',   pre = { 501008 } },     -- Cicero, Artisanry
        { 1008805,  e = CP .. '3',  f = LP .. 'cook',       pre = { 501004 } },     -- Halva, Culinary
        { 21707007, e = CP .. '9',  f = LP .. 'tailor',     pre = { 501009 } },     -- Maina, Weaving
        -- Guild Center (scene 12000, no scenic photo): the guild building's own art; services locked without a guild
        { 1200004,  e = 'ui/atlas/map/union_talk_06', f = TAB .. '83',
          bg = 'ui/textures/union_2/union_device03' },                              -- Kien, Grocery Store (type '21')
        -- Starland (scene 11): its West Beach 'Sea Breeze Secret' scenic-spot postcard (MapStickerTable 110015)
        { 1011500,  e = 'ui/atlas/map/union_talk_37', f = 'ui/atlas/buff/buff_hideseek_icon02',
          bg = 'ui/textures/scenicspot/pic_scenicspotreputation_notepostcard_jizhuangxiangduomaomao' },   -- Meowflyn 'Tag Ambassador' (Starland, scene 11): Wondrous Tag
      }
      local npcs = st.npcs
      if not npcs then
        npcs = {}
        local NPT, IT = TM.GetTable('NpcTableMgr'), TM.GetTable('InteractiveTableMgr')
        -- a pair / condition list read defensively: {{a, b…}, …}, or one level deeper like ActionStage (§13 — the
        -- JSON's 2 levels were 3 live there); an entry whose [1] is not a plain value is unwrapped
        local function flat(c)
          local out = {}
          for _, v in ipairs(c or {}) do
            local k = type(v[1])
            if k ~= 'number' and k ~= 'string' then for _, w in ipairs(v) do out[#out + 1] = w end else out[#out + 1] = v end
          end
          return out
        end
        for _, row in ipairs(NPCS) do pcall(function()
          local nid = row[1]
          local n = NPT.GetRow(nid, true)
          if not n or (n.Name or '') == '' then return end
          local svc, seen = {}, {}
          -- trig = the interaction's TriggerCondition minus its [14, fid] function checks (funcOn covers those): the
          -- event-window conditions the game hides the button on (DhNpcHiddenLua)
          local function add(fid, trig)
            fid = tonumber(fid)
            if not fid or fid <= 0 or seen[fid] then return end
            seen[fid] = true
            local fr = FTn.GetRow(fid, true)
            svc[#svc + 1] = { fid, fr and fr.Name or '', trig, (fr and fr.ParentId == 800981) or false }
          end
          for _, fid in ipairs(row.pre or {}) do add(fid) end
          for _, t in ipairs(n.InteractionTemplate or {}) do
            -- entries are {state, interactionId} (Mael) OR just {interactionId} (guild NPCs, Kien live 2026-11-21:
            -- {{2030},{2025},{3002}}) → the id is the LAST element
            local ir = IT.GetRow(tonumber(t[#t]) or 0, true)
            -- ActionStage = a list of STAGES, each a list of actions {type, param} (live 2026-11-20: 2605 =
            -- {{{'4','800527'}}}); ['4', fid] = EInteractionActionFunctionEntry, the first one is the button's page
            -- ['21', buildId, fid, …] = EInteractionActionUnionBuildFunc (guild NPCs, Kien): buildId 5 = E.UnionBuildId.Mall
            -- is just unlock check + GoToFunc(fid, uuid) (interaction_action.lua:307-312 → union_vm.lua:2129-2168; uuid only
            -- feeds the exchange dialog camera) → the same remote open as type 4. Other buildIds, and type '20' UnionBuild
            -- (the building UPGRADE window, needs the guild scene's building entity), are skipped.
            local fid
            if ir then for _, stage in ipairs(ir.ActionStage or {}) do
              for _, s in ipairs(stage) do
                local ty = tostring(s[1])
                if ty == '4' then fid = tonumber(s[2]) break end
                if ty == '21' and tonumber(s[2]) == 5 then fid = tonumber(s[3]) break end
              end
              if fid then break end
            end end
            if fid then
              local trig
              for _, c in ipairs(flat(ir.TriggerCondition)) do
                if tonumber(c[1]) ~= 14 then trig = trig or {} trig[#trig + 1] = c end
              end
              add(fid, trig)
            end
          end
          -- NpcFunctionID: {{btnId, fid}, …} as talk_option_vm.createNpcFunctionOption reads it (v[1], v[2]); a
          -- list nested one level deeper is unwrapped by flat, so either shape lists its services
          for _, p in ipairs(flat(n.NpcFunctionID)) do add(p[2]) end
          local banner
          if type(row.e) == 'function' then banner = try(row.e, '') else banner = row.e or '' end
          -- thumb marker = NpcTable.MapIcon; empty (Lunel 1008599) → the first service's FunctionTable DialogIcon (the
          -- talk-option icon), else its Icon (the tab icon)
          local thumb = n.MapIcon or ''
          if thumb == '' and svc[1] then
            local fr = FTn.GetRow(svc[1][1], true)
            -- try(): a field missing on the row must not drop the whole NPC (this runs inside its pcall)
            if fr then
              thumb = tostring(try(function() return fr.DialogIcon end, ''))
              if thumb == '' then thumb = tostring(try(function() return fr.Icon end, '')) end
            end
          end
          -- an own backdrop falls back to the shared Asterleeds chain if it does not load (own → AST, one chain string)
          local bg = AST
          if row.bg then bg = { row.bg } for _, p in ipairs(AST) do bg[#bg + 1] = p end end
          bg = table.concat(bg, '|')
          npcs[#npcs + 1] = { nid, n.Name, n.Title or '', svc, banner, row.f or '', thumb, bg }
        end) end
        -- tables not loaded yet → nothing cached, the next full poll reads them again
        if #npcs > 0 then st.npcs = npcs end
      end
" + DhNpcHiddenLua + @"
      for _, n in ipairs(npcs) do
        local key = 'n' .. n[1]
        rec('E', 4, key, n[2], '', '', 0, n[3], 0, 0, 0, 0, 0, '', 0, '', '', 0, 0, 0)
        rec('N', key, n[5], n[6], n[7], n[8])
        local slot = 0
        for _, s in ipairs(n[4]) do
          local on = funcOn(s[1])
          if not hidden(s[1], on, s[3]) then rec('S', key, slot, s[1], s[2], b(not on), b(s[4])) slot = slot + 1 end
        end
      end
    end)
";

    // hidden(fid, on, trig) — an event / time-limited service that is off right now is left out of the list (the game
    // hides its NPC button then), user 2026-11-21. Recomputed on every FULL poll (cheap: a few table rows per service).
    // The game's own gates, re-read the way it reads them:
    //   • trig (the interaction's non-[14] TriggerCondition, the button's visibility) fails → hidden. Kalista's Renewal
    //     Store 731036 = [9, 40059], condition_helper.lua:186 checkConditionType_9 = inside a TimerId window.
    //   • the function is off for a NON-unlock reason, on it or any ParentId up the chain (switch_vm.lua:162-181
    //     CheckFuncSwitch walks the same chain): OnOff = 1 (:170), the server / per-user close lists
    //     switch_data.ServerCloseFunctionIdDic / UserCloseFunctionIdDic (checkFunctionIsClose :17-35 — how the server
    //     turns a function off without a table change), or a FunctionTable TimerId whose window is not open now
    //     (CheckTime :37). A level / quest lock is none of these → it stays, shown Locked.
    //   • LIMITED: the three known limited-event functions, hidden whenever off, whatever the reason. 200508 / 200511 have
    //     no TimerId and only a [14, fid] trigger (InteractiveTable 2523 / 2540), so the static tables can't tell their
    //     event off from a plain lock; this list is the backstop (an id here never shows Locked).
    private const string DhNpcHiddenLua = @"
      local SD = try(function() return Z.DataMgr.Get('switch_data') end, nil)
      local LIMITED = { [200508] = true, [200511] = true, [819305] = true }   -- Skill Reset / Imagine Conversion events, Renewal Store
      local function timerOff(tid)
        local s, e = Z.TimeTools.GetWholeStartEndTimeByTimerId(tid)
        if not s or s <= 0 then return false end
        local now = Z.TimeTools.Now() / 1000
        return now < s or (e ~= nil and e > 0 and now > e)
      end
      local function closedOrTimed(fid)
        local id = fid
        for _ = 1, 8 do
          local r = FTn.GetRow(id, true)
          if not r then return false end
          if r.OnOff == 1 then return true end
          if SD and ((SD.ServerCloseFunctionIdDic or {})[id] or (SD.UserCloseFunctionIdDic or {})[id]) then return true end
          if (r.TimerId or 0) ~= 0 and try(function() return timerOff(r.TimerId) end, false) then return true end
          id = r.ParentId
          if not id or id == 0 then return false end
        end
        return false
      end
      local function hidden(fid, on, trig)
        if trig and not condOk(trig) then return true end
        if on then return false end
        return LIMITED[fid] or closedOrTimed(fid) or false
      end
";

    // Open one service page. Parks __stellarDHOpen = 'OK' / 'OK fallback …' / 'NO locked|here|failed …' / 'ERR …'.
    // GoToFunc runs the full CheckFuncCanUse with the game's own tip; the silent pre-checks only tell C# WHY it failed.
    // A life-skill craft (DhLifeCraftPro) runs that same CheckFuncCanUse (tip on refusal), then the workbench's CookFunc
    // dispatch verbatim (Cook → cook.OpenCookView(cam); Chemistry → chemistry.OpenChemistryMainView(cam, false); else
    // life_profession.OpenCastMainView(pro, cam, false) — isHouseCast false, as the world benches pass param[3] ~= 1);
    // its 'OK life …' result carries proId, camId and the live FunctionOrder so the log shows what GoToFunc would have done.
    private static string DhNpcOpenChunk(int funcId) => DhOpenWrap(
        " local fid = " + Q(funcId) +
        " local gv = Z.VMMgr.GetVM('gotofunc')" +
        " local on = gv.FuncIsOn(fid, true)" +
        " local can = on and gv.CheckFuncCanUse(fid, true)" +
        " local lc = (" + DhLifeCraftPro + ")[fid]" +
        " if lc then" +
        "   local pro, cam = lc[1], lc[2]" +
        "   if not gv.CheckFuncCanUse(fid, false) then res = on and 'NO here' or 'NO locked' return end" +
        "   local LP = E.ELifeProfession or {}" +
        "   if pro == (LP.Cook or 201) then Z.VMMgr.GetVM('cook').OpenCookView(cam)" +
        "   elseif pro == (LP.Chemistry or 202) then Z.VMMgr.GetVM('chemistry').OpenChemistryMainView(cam, false)" +
        "   else Z.VMMgr.GetVM('life_profession').OpenCastMainView(pro, cam, false) end" +
        "   local fo = '' pcall(function() for _, v in ipairs(Z.TableMgr.GetTable('FunctionTableMgr').GetRow(fid).FunctionOrder) do fo = fo .. tostring(v) .. ' ' end end)" +
        "   res = 'OK life profession ' .. pro .. ' cam ' .. cam .. ' (FunctionOrder ' .. fo .. ')' return" +
        " end" +
        " local went = gv.GoToFunc(fid)" +
        " if went then res = 'OK' return end" +
        " if not on then res = 'NO locked' return end" +
        " if not can then res = 'NO here' return end" +
        " if fid == ((E.FunctionID and E.FunctionID.SeasonTalentFactorRefine) or " + Q(DhMatrixFunc) + ") then" +
        "   Z.VMMgr.GetVM('season_talent').OpenRefineView() res = 'OK fallback OpenRefineView (GoToFunc = ' .. tostring(went) .. ')' return end" +
        " res = 'NO failed GoToFunc = ' .. tostring(went)");

    // S: key, slot, funcId, name, locked, material shop. A life-skill material shop carries its craft's name
    // ("Alchemy" for both), so it gets our own "{0} Material Shop" line — resolved here, at parse, so it follows a
    // language switch on the next catalog poll like the subtitle.
    private void DhParseSvc(DhEntry e, string[] f)
    {
        var list = e.Services ??= new List<DhService>(DhNpcSlots);
        if (list.Count >= DhNpcSlots) return;   // more than the fixed rows: not shown (none today; Cassie has exactly 7)
        var name = f[4];
        if (f.Length > 6 && f[6] == "1") name = LF("dh.npc.materialShop", name);
        list.Add(new DhService { FuncId = DcInt(f[3]), Name = name, Locked = f[5] == "1" });
    }

    // Per-NPC subtitle override (user-approved 2026-11-21): the game's NpcTable.Title says what the NPC IS ("Legacy
    // Expert"), not what it's FOR, so an NPC may carry our own catalog line "dh.npc.title.<npcId>" (Mael 1008600 =
    // "Factor Merchant"). No key → the game title stays, so a newly listed NPC needs no catalog work. Resolved
    // at parse time (DhParse, entry key 'n<npcId>') like the rest of the sub-line, so it follows a language switch on
    // the next catalog poll. ILocalization.T returns the key literal for an unknown key — that is the "no override" test.
    private string DhNpcTitle(string entryKey, string gameTitle)
    {
        var key = "dh.npc.title." + entryKey.Substring(1);
        var t = L(key);
        return t == key ? gameTitle : t;
    }

    private DhService? DhSvcAt(int i) => DhCur is { IsNpc: true, Services: { } s } && i < s.Count ? s[i] : null;

    // ── press ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Success hides the hub (the page opens behind it, like an activity's Open). A refusal keeps it open with a short
    // reason in the bottom bar; the game has already shown its own tip. One log line per failure (or fallback), no more.
    private void DhOnNpcOpen(int i)
    {
        var e = DhCur; var s = DhSvcAt(i);
        if (e == null || s == null || s.Locked || !_services.Lua.Ready) return;
        _services.Lua.DoString(DhNpcOpenChunk(s.FuncId));
        var res = _services.Lua.ReadGlobalString("__stellarDHOpen") ?? "(no result)";
        if (res.StartsWith("OK", StringComparison.Ordinal))
        {
            if (res.Length > 2) _services.Log.Info($"[DungeonHub] npc {e.Key} open func={s.FuncId} ({s.Name}): {res}");
            DhHide();
            return;
        }
        _services.Log.Warning($"[DungeonHub] npc {e.Key} open func={s.FuncId} ({s.Name}) failed: {res}");
        DhSetNotice(LF(res.StartsWith("NO locked", StringComparison.Ordinal) ? "dh.npc.locked"
            : res.StartsWith("NO here", StringComparison.Ordinal) ? "dh.npc.notHere" : "dh.npc.failed", s.Name));
    }

    // ── panel: art band · name · title · SERVICES · one 54 px row per service (7) ─────────────────────────────────
    // No NPC portrait exists in the tables (NpcTable.NpcIcon is empty for Mael, ModelNpcTable holds only model parts),
    // so the band shows the NPC's ART banner (a page's art) over its scenery backdrop (.NpcArt.cs), or the empty-art tile.
    private HudElement DhNpcPanel()
    {
        var slots = new HudElement[DhNpcSlots];
        for (int i = 0; i < DhNpcSlots; i++)
        {
            int k = i;
            // Two WHOLE bubbles swapped by a Conditional (never a Conditional inside a bubble's measured child,
            // WindowBuilder-Patterns.md §BubbleElement); an empty slot collapses, the block's strut keeps the height.
            slots[i] = new ConditionalElement(() => DhSvcAt(k) != null,
                new ConditionalElement(() => DhSvcAt(k) is { Locked: true }, DhSvcRow(k, locked: true), DhSvcRow(k, locked: false)));
        }
        return DhPanelNoCaption(
            DhArtBand(),
            DhNpcHead(),   // name · title + the Pin pill (.NpcPins.cs); same height as the old name + title rows
            DhCaption(() => L("dh.cap.services")),
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DhSvcBlockH),
                new CellElement(DhTopPinned(new ColumnElement(slots, Gap: DhSvcGap)), Width: DhDetInW),
            }, Gap: 0.01f));
    }

    // One service row (mock .sv): Tile fill, bold game name, accent Open pill on the right. Locked (.sv.off): the tile
    // dimmed like a locked list row, the name muted + a muted "Locked" word (plain text: status is never a pill, house
    // rule 22), and a RECESSED Open that does nothing (sunken fill, dim label, no ring).
    private HudElement DhSvcRow(int i, bool locked)
    {
        Func<ColorRgba> fill = locked ? () => Mix(PanelColor(), DcTile(), 0.5f) : DcTile;
        HudElement name = new TextElement(() => DhSvcAt(i)?.Name ?? "", Color: () => locked ? MutedColor() : TextColor(), NoWrap: true,
            FontSize: DhFsName) { Bold = true, Ellipsize = true };
        if (locked) name = new RowElement(new HudElement[]
        {
            name, new SpacerElement(DhSvcTagGap, 0f), DcSmall(() => L("dh.word.locked"), MutedColor, DhFsSub),
        }, Gap: 0.01f);
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(DhSvcPadL, DhSvcH),
            new CellElement(name, Weight: 1f),
            locked ? DhRecessedPill(() => L("dh.act.open"), fill, DhCancelW, DhChipH)
                   : DhPill(() => L("dh.act.open"), () => Menu.MenuAccent, () => DcOn(White, Menu.MenuAccent), () => DhOnNpcOpen(i), DhCancelW, DhChipH),
            new SpacerElement(DhSvcPadR, 0f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(fill()), CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhDetInW,
        };
    }

    // Disabled = recessed (Stellar-Design-Language rule 22): black@0.28 sunk into its surface, label Muted@0.45 over that
    // fill, no ring, same size as the live pill. Not clickable (no SelectableElement).
    private HudElement DhRecessedPill(Func<string> label, Func<ColorRgba> surface, float w, float h)
    {
        Func<ColorRgba> sunk = () => Over(new ColorRgba(0f, 0f, 0f, 0.28f), surface());
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, h), DcDrop(DcBold(label, () => Over(MutedColor() with { A = 0.45f }, sunk()), DhFsSmall, w, TextAlign.Center)),
            }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fade(sunk()), CornerRadius = h / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            }, Width: w);
    }
}
