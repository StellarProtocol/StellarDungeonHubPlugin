using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// NPC tab — pinned NPCs + search (2026-11-21, user-approved _scratch\prototypes\dungeon-hub-npc-pins-APPROVED.html).
//
//   view     the NPC tab's list is a VIEW (_dhNpcView) of the catalog list _dhLists[DhTabNpc]: .Model.cs DhList returns
//            it on the NPC tab, so the slots, DhAt, DhCur and the selection all work on what is shown. Rebuilt ONLY when
//            its inputs change — a full catalog parse, a keystroke in the search, a pin toggle (DhNpcRebuildView) — never
//            per frame; the per-frame reads are a list index and a HashSet lookup.
//   order    not searching: pinned NPCs in PIN order under "PINNED", then the rest in game order under "ALL"; nothing
//            pinned → no captions (the list looks as before). Searching: one flat list of matches, pinned first (pin
//            order) then the rest, no captions.
//   search   case-insensitive substring of the NPC NAME or its sub-line (e.Sub = our dh.npc.title.<id> override or the
//            game title), trimmed. NOT service names (user 2026-11-21: name + description only).
//   select   a selected NPC the search filters out → the first match (DhFixSelection after each rebuild). No match →
//            the detail keeps the last NPC (.Model.cs DhCur falls back to the full list) and the list shows one
//            recessed "No NPC matches" row (slot 0, .Window.cs DhListSlot).
//   persist  config "dungeon_hub_npc_pins" = npcIds comma-joined, in pin order. An id no longer listed is ignored (kept
//            in the config: a briefly partial catalog must not drop pins).
//
// ★ marks are generated PNGs (white, tinted with ImageElement.Tint): the game fonts can't be trusted with ★ / ☆ — a
// BOLD "✕" already drew blank (Stellar-Design-Language.md §6).
public sealed partial class Plugin
{
    private const string DhPinsCfgKey = "dungeon_hub_npc_pins";
    private static readonly ColorRgba DhGold = Rgb(0xffd27f);

    // ── geometry (mock px → ×1.6, DcS / DcSE only: see the static-order note in .Window.cs) ─────────────────────────
    private static readonly float DhStarSlotW = DcSE(16f), DhStarRowW = DcSE(12f);                         // 26 · 20
    private static readonly float DhPinW = DcSE(100f), DhPinStarGap = DcS(5f);                           // 160 · 8
    // The search field is the SDK InputElement: a fixed 28 px box (UGuiTextInput boxHeight) = the list caption strut
    // DhCapH, so swapping the caption for the search keeps the list panel one height on every tab. The ✕ clear is a
    // 28 px round icon button in a fixed slot beside it (the InputElement can't host children).
    private static readonly float DhClearW = DcSE(18f), DhClearGlyph = DcSE(8f);                           // 28 · 12

    private readonly List<string> _dhPins = new();            // entry keys 'n<npcId>', in pin order
    private readonly HashSet<string> _dhPinSet = new();
    private readonly List<DhEntry> _dhNpcView = new();
    private int _dhNpcPinnedShown;                            // pinned entries at the top of the view (caption split)
    private string _dhNpcQuery = "", _dhNpcQueryTrim = "", _dhNpcNoMatchText = "";

    private bool DhNpcSearching => _dhNpcQueryTrim.Length > 0;
    private bool DhNpcNoMatch => _dhTab == DhTabNpc && DhNpcSearching && _dhNpcView.Count == 0;
    private bool DhIsPinned(DhEntry? e) => e != null && _dhPinSet.Contains(e.Key);

    private void DhNpcLoadPins()
    {
        _dhPins.Clear(); _dhPinSet.Clear();
        var raw = _cfg.Get<string>(DhPinsCfgKey, "") ?? "";
        foreach (var part in raw.Split(','))
        {
            var id = part.Trim();
            if (id.Length == 0 || !int.TryParse(id, out _)) continue;
            var key = "n" + id;
            if (_dhPinSet.Add(key)) _dhPins.Add(key);
        }
    }

    private void DhNpcSavePins()
    {
        var ids = new string[_dhPins.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = _dhPins[i].Substring(1);
        _cfg.Set<string>(DhPinsCfgKey, string.Join(",", ids));
        _cfg.Save();
    }

    private void DhTogglePin()
    {
        var e = DhCur;
        if (e is not { IsNpc: true }) return;
        if (_dhPinSet.Remove(e.Key)) _dhPins.Remove(e.Key);
        else { _dhPinSet.Add(e.Key); _dhPins.Add(e.Key); }
        DhNpcSavePins();
        DhNpcRebuildView();
    }

    private void DhSetNpcQuery(string s)
    {
        s ??= "";
        if (s == _dhNpcQuery) return;
        _dhNpcQuery = s;
        var trim = s.Trim();
        if (trim == _dhNpcQueryTrim) return;   // only spaces changed: same matches
        _dhNpcQueryTrim = trim;
        DhNpcRebuildView();
    }

    private bool DhNpcMatches(DhEntry e) => !DhNpcSearching ||
        e.Name.IndexOf(_dhNpcQueryTrim, StringComparison.OrdinalIgnoreCase) >= 0 ||
        e.Sub.IndexOf(_dhNpcQueryTrim, StringComparison.OrdinalIgnoreCase) >= 0;

    // Pinned (pin order) then the rest (game order), both filtered by the search. Called from DhParse (full catalog),
    // a query change and a pin toggle; re-fixes the selection when the NPC tab is showing.
    private void DhNpcRebuildView()
    {
        var all = _dhLists[DhTabNpc];
        _dhNpcView.Clear();
        foreach (var key in _dhPins)
            foreach (var e in all)
                if (e.Key == key) { if (DhNpcMatches(e)) _dhNpcView.Add(e); break; }
        _dhNpcPinnedShown = _dhNpcView.Count;
        foreach (var e in all)
            if (!_dhPinSet.Contains(e.Key) && DhNpcMatches(e)) _dhNpcView.Add(e);
        _dhNpcNoMatchText = DhNpcSearching ? LF("dh.npc.noMatch", _dhNpcQueryTrim) : "";
        if (_dhTab == DhTabNpc && _dhNpcView.Count > 0) DhFixSelection();
    }

    // "PINNED" over the first pinned row, "ALL" over the first other row — only when something is pinned and the
    // search is empty (.Window.cs DhGroupHeader).
    private string DhNpcGroupHeader(int i)
    {
        if (DhNpcSearching || _dhNpcPinnedShown == 0 || i >= _dhNpcView.Count) return "";
        if (i == 0) return L("dh.npc.pinnedCap");
        return i == _dhNpcPinnedShown ? L("dh.npc.all") : "";
    }

    // ── search row (replaces the list caption on the NPC tab; same 28 px strut) ──────────────────────────────────────
    private HudElement DhNpcSearchRow()
    {
        float inW = DhListW - 2f * DhPanPadX - DhClearW - DcS(4f);
        return new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhCapH),
            new CellElement(new InputElement(() => _dhNpcQuery, DhSetNpcQuery, inW, OnChange: DhSetNpcQuery)
            {
                SingleLine = true,   // one fixed 28 px line: a long paste scrolls sideways instead of growing the panel
                Placeholder = () => L("dh.npc.search"),
            }, Width: inW),
            new SpacerElement(DcS(4f), 0f),
            // Fixed slot: the field never changes width when the ✕ appears.
            new CellElement(new ConditionalElement(() => _dhNpcQuery.Length > 0, DhNpcClearButton(), new SpacerElement(DhClearW, DhClearW)),
                Width: DhClearW),
        }, Gap: 0.01f);
    }

    // Icon-only ✕ (house §4, the header ✕ at 28 px): no fill at rest, text-wash on hover; clears the search.
    private HudElement DhNpcClearButton()
    {
        var hover = new DcHover();
        float side = (DhClearW - DhClearGlyph) / 2f;
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(side, DhClearW),
                new ImageElement(() => DcGlyphPng(DcGlyph.Cross, DhClearGlyph, hover.On ? TextColor() : MutedColor()), (int)DhClearGlyph, (int)DhClearGlyph),
                new SpacerElement(side, DhClearW),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: () => DhSetNpcQuery("")) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(hover.On ? Over(Menu.MenuText with { A = 0.10f }, PanelColor()) : Transparent),
                CornerRadius = DhClearW / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhClearW,
            };
    }

    // No match (mock .empty): one recessed row the size of a list row — a shade between the panel and a tile, muted
    // centred text. Text built at rebuild time, not per frame.
    private HudElement DhNpcEmptyRow() => new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhRowH),
            DcSmall(() => _dhNpcNoMatchText, MutedColor, DhFsSmall, DhRowW - DcSE(16f), TextAlign.Center),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(Mix(PanelColor(), DcTile(), 0.2f)), CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhRowW,
        };

    // The ★ slot at a list row's right edge: constant width on every NPC row, the star tinted transparent when the row
    // isn't pinned (house: per-row status icon = fixed cell + Tint, never a Conditional → no row jump).
    private HudElement DhNpcRowStar(int i) => new CellElement(new RowElement(new HudElement[]
        {
            new ImageElement(DhStarFillPng, (int)DhStarRowW, (int)DhStarRowW)
                { Tint = () => DhIsPinned(DhAt(i)) ? DhGold : Transparent },
        }, Gap: 0.01f, Justify: RowJustify.Center), Width: DhStarSlotW);

    // ── detail head: name over sub-line, Pin / Pinned pill on the right ──────────────────────────────────────────────
    // The pair keeps the panel's old rows exactly (name 34 · gap · sub 26 = one 70 px block), so the services block
    // stays where it was; the 44 px pill is centred on it.
    private HudElement DhNpcHead() => new RowElement(new HudElement[]
    {
        new CellElement(new ColumnElement(new HudElement[]
        {
            DhNameRow(),
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DhActSubH),
                new CellElement(new TextElement(() => DhCur?.Sub ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub)
                    { Ellipsize = true }, Weight: 1f),
            }, Gap: 0.01f),
        }, Gap: DhPanGap), Width: DhDetInW - DhPinW - DhPanGap),
        new SpacerElement(DhPanGap, 0f),
        new CellElement(new ConditionalElement(() => DhIsPinned(DhCur), DhPinPill(pinned: true), DhPinPill(pinned: false)), Width: DhPinW),
    }, Gap: 0.01f);

    // Pin (mock .bn): neutral pill, outline star + "Pin" in text ink. Pinned (mock .bp): 18 % gold wash, 1 px gold@0.5
    // ring, filled gold star + gold "Pinned". Two WHOLE bubbles swapped by the caller's Conditional (bold text and the
    // ring can't toggle inside one measured bubble). Hover = 10 % text wash, like DhPill.
    private HudElement DhPinPill(bool pinned)
    {
        var hover = new DcHover();
        float h = DhChipH;
        Func<ColorRgba> fill = pinned ? () => Over(DhGold with { A = 0.18f }, PanelColor()) : DcNeutral;
        Func<ColorRgba> ink = pinned ? () => EnsureContrast(DhGold, fill(), 4.5f) : () => EnsureContrast(TextColor(), fill(), 4.5f);
        // Centring = Dungeon Confirm's "Confirmed ✓" pill (.Art.cs DcConfirmedPill): a zero-height spacer pins the
        // Column to the pill's inner width, the natural-width star + label pair centres in the Row under it.
        float pad = DcS(8f);
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(pad, h),
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(DhPinW - 2f * pad, 0f),
                    new RowElement(new HudElement[]
                    {
                        new SpacerElement(0f, h),
                        new ImageElement(() => pinned ? DhStarFillPng() : DhStarLinePng(), (int)DhStarRowW, (int)DhStarRowW) { Tint = () => ink() },
                        new SpacerElement(DhPinStarGap, 0f),
                        DcDrop(DcBold(() => L(pinned ? "dh.npc.pinned" : "dh.npc.pin"), ink, DhFsSmall)),
                    }, Gap: 0.01f, Justify: RowJustify.Center),
                }, Gap: 0.01f),
                new SpacerElement(pad, h),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: DhTogglePin) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(hover.On ? Over(Menu.MenuText with { A = 0.10f }, fill()) : fill()),
                OutlineColorFn = () => FadeRing(pinned ? DhGold with { A = 0.5f } : null),
                CornerRadius = h / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhPinW,
            };
    }

    // ── star PNGs (white, 2× the largest shown size; tinted by ImageElement.Tint) ───────────────────────────────────
    // Same 24-grid star as Dungeon Confirm's leader mark (.Art.cs DcStarPng / DcStarPt): filled = even-odd inside the
    // 10-point polygon; outline (☆) = within 1.5 units of any of its 10 edges (a 3-unit stroke centred on the edge).
    private static byte[]? _dhStarFill, _dhStarLine;

    private static byte[]? DhStarFillPng() => _dhStarFill ??= DcRaster(2 * (int)DhStarRowW, 24f, 255, 255, 255, DhStarInside);

    private static byte[]? DhStarLinePng() => _dhStarLine ??= DcRaster(2 * (int)DhStarRowW, 24f, 255, 255, 255, (x, y) =>
    {
        for (int i = 0, j = 9; i < 10; j = i++)
        {
            DcStarPt(i, out var xi, out var yi); DcStarPt(j, out var xj, out var yj);
            if (DcSeg(x, y, xi, yi, xj, yj, 1.5f)) return true;
        }
        return false;
    });

    private static bool DhStarInside(float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = 9; i < 10; j = i++)
        {
            DcStarPt(i, out var xi, out var yi); DcStarPt(j, out var xj, out var yj);
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}
