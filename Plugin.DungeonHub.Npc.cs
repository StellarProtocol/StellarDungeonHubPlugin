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
//   service  each InteractiveTable row of that list whose ActionStage holds ['4', fid] (= EInteractionActionFunctionEntry,
//            whose handler is just gotofunc.GoToFunc(fid) with no NPC argument, interaction_action.lua:24-30); its name
//            = FunctionTable[fid].Name. Other interactions (talk 3002, …) are skipped. The 3 m TriggerRange and the
//            Condition [14, fid] only gate the button's visibility next to the NPC.
//   locked   gotofunc.FuncIsOn(fid, true) = switch.CheckFuncSwitch(fid) — the unlock alone, no tip. NOT
//            CheckFuncCanUse(fid, true): that adds the scene / buff / dead checks and would show every row Locked while
//            standing in a dungeon (the raid list's reason for the same choice, §8 v4). The press re-checks it all.
//   open     gotofunc.GoToFunc(fid) — CheckFuncCanUse(fid, false) (the game's own tip on refusal) + FunctionOrder.
//            800527 Matrix Manifestation only: GoToFunc false WITHOUT a lock / scene reason → season_talent
//            OpenRefineView() (the Refine tab of season_talent_function_main, §13).
//
// Records (FULL poll only, after the activities): E (tab 4, key 'n<npcId>', name, sub = the NPC title, or our DhNpcTitle override —
//   also the list row's whole sub-line, user 2026-11-21: the old " · N services" tail got truncated) and per service
//   S  key, slot (0-based, NPC order), funcId, name, locked
//   N  key, banner, banner fallback, thumb — the NPC's art paths (.NpcArt.cs); banner from ART below, thumb = NpcTable.MapIcon
// The NpcTable / InteractiveTable / FunctionTable walk runs ONCE per login (cached on the hub's Lua stash st.npcs, a new
// Z.UIMgr on relog starts clean); a full poll then only re-reads each service's lock.
public sealed partial class Plugin
{
    private const int DhNpcSlots = 6;   // fixed service rows: nothing shifts between NPCs (Mael has 5)
    private const int DhMatrixFunc = 800527;   // E.FunctionID.SeasonTalentFactorRefine (Matrix Manifestation)

    private sealed class DhService { public int FuncId; public string Name = ""; public bool Locked; }

    // ── geometry (mock px → ×1.6, DcS / DcSE only: see the static-order note in .Window.cs) ─────────────────────────
    private static readonly float DhSvcH = DcSE(40f), DhSvcGap = DcS(4f);                                  // 64 · 6
    private static readonly float DhSvcPadL = DcS(10f), DhSvcPadR = DcS(6f), DhSvcTagGap = DcS(6f);        // 16 · 10 · 10
    // The services block takes the rest of the panel column (art · name · title · SERVICES caption · block, 4 gaps), so
    // the panel is exactly as tall as every other tab's. A PROPERTY, not a static readonly: it reads .Window.cs fields,
    // whose initialisers may not have run yet when this file's do (partial-file static order is unspecified).
    private static float DhSvcBlockH => DhColsH - 2f * DhPanPadY - DhArtH - DhNameH - DhActSubH - DhCapH - 4f * DhPanGap;   // 436 ≥ 6 × 64 + 5 × 6

    private const string DhNpcCatalogLua = @"
    -- NPC tab (tab 4, .Npc.cs). NPCS = the NpcTable ids listed, in this order; add an id to list another service NPC.
    pcall(function()
      local NPCS = { 1008600 }   -- Mael 'Legacy Expert': Factor Dismantle / Exchange, Matrix Manifestation, Imprint Embedding / Inheritance
      -- ART[npcId] = { banner, fallback } (no entry = the empty band). Mael: the Matrix Manifestation page's art —
      -- Z.Global.SeasonTalentEntryStorageIcon = {{seasonId, path}, …}, the pair of the current season as
      -- season_talent_refine_sub_view.refreshTalentStorageIcon picks it (no match → the last pair, then the S4 literal);
      -- fallback = the season emblem (SeasonGlobalTable.SeasonLevelIcon).
      local function seasonArt()
        local sid = tostring(try(function() return Z.VMMgr.GetVM('season').GetCurrentSeasonId() end, ''))
        local hit, last
        pcall(function() for _, v in ipairs(Z.Global.SeasonTalentEntryStorageIcon) do
          last = v[2] if tostring(v[1]) == sid then hit = v[2] end
        end end)
        return hit or last or 'ui/textures/season_talent/season_talent_icon_s4'
      end
      local ART = { [1008600] = { seasonArt, 'ui/atlas/season_talent/img_seasontalent_main_talent4' } }
      local npcs = st.npcs
      if not npcs then
        npcs = {}
        local NPT, IT, FTn = TM.GetTable('NpcTableMgr'), TM.GetTable('InteractiveTableMgr'), TM.GetTable('FunctionTableMgr')
        for _, nid in ipairs(NPCS) do pcall(function()
          local n = NPT.GetRow(nid, true)
          if not n or (n.Name or '') == '' then return end
          local svc = {}
          for _, t in ipairs(n.InteractionTemplate or {}) do
            local ir = IT.GetRow(tonumber(t[2]) or 0, true)
            -- ActionStage = a list of STAGES, each a list of actions {type, param} (live 2026-11-20: 2605 =
            -- {{{'4','800527'}}}); ['4', fid] = EInteractionActionFunctionEntry, the first one is the button's page
            local fid
            if ir then for _, stage in ipairs(ir.ActionStage or {}) do
              for _, s in ipairs(stage) do if tostring(s[1]) == '4' then fid = tonumber(s[2]) break end end
              if fid then break end
            end end
            if fid then svc[#svc + 1] = { fid, try(function() return FTn.GetRow(fid, true).Name end, '') } end
          end
          local art = ART[nid] or {}
          local banner = art[1] and try(art[1], '') or ''
          npcs[#npcs + 1] = { nid, n.Name, n.Title or '', svc, banner, art[2] or '', n.MapIcon or '' }
        end) end
        -- tables not loaded yet → nothing cached, the next full poll reads them again
        if #npcs > 0 then st.npcs = npcs end
      end
      for _, n in ipairs(npcs) do
        local key = 'n' .. n[1]
        rec('E', 4, key, n[2], '', '', 0, n[3], 0, 0, 0, 0, 0, '', 0, '', '', 0, 0, 0)
        rec('N', key, n[5], n[6], n[7])
        for i, s in ipairs(n[4]) do rec('S', key, i - 1, s[1], s[2], b(not funcOn(s[1]))) end
      end
    end)
";

    // Open one service page. Parks __stellarDHOpen = 'OK' / 'OK fallback …' / 'NO locked|here|failed …' / 'ERR …'.
    // GoToFunc runs the full CheckFuncCanUse with the game's own tip; the silent pre-checks only tell C# WHY it failed.
    private static string DhNpcOpenChunk(int funcId) => DhOpenWrap(
        " local fid = " + Q(funcId) +
        " local gv = Z.VMMgr.GetVM('gotofunc')" +
        " local on = gv.FuncIsOn(fid, true)" +
        " local can = on and gv.CheckFuncCanUse(fid, true)" +
        " local went = gv.GoToFunc(fid)" +
        " if went then res = 'OK' return end" +
        " if not on then res = 'NO locked' return end" +
        " if not can then res = 'NO here' return end" +
        " if fid == ((E.FunctionID and E.FunctionID.SeasonTalentFactorRefine) or " + Q(DhMatrixFunc) + ") then" +
        "   Z.VMMgr.GetVM('season_talent').OpenRefineView() res = 'OK fallback OpenRefineView (GoToFunc = ' .. tostring(went) .. ')' return end" +
        " res = 'NO failed GoToFunc = ' .. tostring(went)");

    private void DhParseSvc(DhEntry e, string[] f)
    {
        var list = e.Services ??= new List<DhService>(DhNpcSlots);
        if (list.Count >= DhNpcSlots) return;   // more than the fixed rows: not shown (none today)
        list.Add(new DhService { FuncId = DcInt(f[3]), Name = f[4], Locked = f[5] == "1" });
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

    // ── panel: art band · name · title · SERVICES · one 64 px row per service ─────────────────────────────────────
    // No NPC portrait exists in the tables (NpcTable.NpcIcon is empty for Mael, ModelNpcTable holds only model parts),
    // so the band shows the NPC's ART banner (a page's art, .NpcArt.cs), or the hub's empty-art tile without one.
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
            DhNameRow(),
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DhActSubH),
                new CellElement(new TextElement(() => DhCur?.Sub ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub)
                    { Ellipsize = true }, Weight: 1f),
            }, Gap: 0.01f),
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
