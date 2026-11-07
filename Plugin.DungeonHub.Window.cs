using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub window — the approved v3 prototype + v4 / v5 changes (_scratch\prototypes\dungeon-hub-v5.html), 700 px at mock
// scale × DcScale 1.6, built from the same house blocks as Dungeon Confirm (Plugin.DungeonConfirm.Window.cs: DcSmall / DcBold / DcDrop, theme-live DcTile / DcNeutral).
// One fixed layout: every row is pinned by a strut, so switching tab, dungeon, difficulty or match state never moves
// anything. Body padding is the framework's fixed 12 per side (DcPad).
//
// ⚠️ Every scaled size is static readonly and lives HERE, built only from DcS / DcSE (static METHODS over the DcScale
// const) — never from Dungeon Confirm's static readonly fields: static initialisers across partial files run in an
// unspecified order, so another file's field could still read 0 (Plugin.DungeonConfirm.Window.cs scale note).
public sealed partial class Plugin
{
    // ── geometry (mock px → ×1.6) ─────────────────────────────────────────────────────────────────────────────────
    private static readonly float DhW = DcSE(700f), DhInW = DcSE(700f) - 24f;                               // 1120 · 1096
    private static readonly float DhGap = DcSE(8f), DhHeaderH = DcSE(40f), DhBarH = DcSE(40f);              // 12 · 64 · 64
    private static readonly float DhTabH = DcSE(24f), DhTabPadY = DcS(3f), DhTabGap = DcSE(4f);             // 38 · 5 · 6
    private static readonly float DhTabW = MathF.Floor((DcSE(700f) - 24f - 12f - 4f * DcSE(4f)) / 5f);     // 212
    private static readonly float DhTabPadX = (DcSE(700f) - 24f - 5f * DhTabW - 4f * DcSE(4f)) / 2f;       // 6
    private static readonly float DhListW = DcSE(250f);                                                     // 400
    private static readonly float DhDetW = DcSE(700f) - 24f - DcSE(250f) - DcSE(8f);                        // 684
    private static readonly float DhPanPadX = DcSE(10f), DhPanPadY = DcS(8f), DhPanGap = DcS(6f);           // 16 · 13 · 10
    private static readonly float DhCapH = DcSE(18f), DhRadius = DcS(6f);                                   // 28 · 10
    // List: rows 84 (thumb 102×68), pitch 84 + 8 + the ListElement's own 2 px = 94; the scroll viewport keeps a ~9 px
    // lane for its bar on the right, so a row is 10 px narrower than the panel's inner width.
    private static readonly float DhRowW = DhListW - 2f * DhPanPadX - 10f, DhRowH = DcSE(52f);             // 358 · 84
    private static readonly float DhThumbW = DcSE(64f), DhThumbH = DcSE(42f);                               // 102 · 68
    private static readonly float DhRowLead = DcSE(4f), DhRowGapX = DcSE(8f);                              // 6 · 12
    private static readonly float DhRowTextW = DhRowW - DhRowLead - DhThumbW - 2f * DhRowGapX;             // 226
    private static readonly float DhRowPitchPad = DcSE(6f) - 2f;                                            // 8
    // Detail: inner 652 × 640 = art 180 · name 34 · caption · chips 44 · the difficulty block 240 · buttons 64.
    private static readonly float DhDetInW = DhDetW - 2f * DhPanPadX;                                       // 652
    private static readonly float DhArtH = DcSE(112f), DhNameH = DcSE(21f);                                 // 180 · 34
    private static readonly float DhChipH = DcSE(28f), DhChipW = DcSE(72f), DhChipWideW = DcSE(92f);        // 44 · 116 · 148
    private static readonly float DhTileH = DcSE(41f), DhBtnH = DcSE(40f), DhBtnRadius = DcS(13f);          // 66 · 64 · 21
    private static readonly float DhThirdW = 2f * MathF.Floor((DcSE(700f) - 24f - DcSE(250f) - DcSE(8f) - 2f * DcSE(10f) - 2f * DcS(6f)) / 6f);   // 210
    private static readonly float DhThirdMidW = DhDetInW - 2f * DhThirdW - 2f * DhPanGap;                  // 212
    private static readonly float DhHalfW = (DhDetInW - DhPanGap) / 2f;                                     // 321
    // Master level grid (v4: 2 rows × 10 buttons, mock 28 px tall, 4 px gaps, equal widths filling the row) + legend.
    // The legend strut is 26 (v4: 22): an 18 px TMP line is taller than 22, so the v4 legend outgrew its strut.
    private static readonly float DhLvH = DcSE(28f), DhLvGap = DcSE(4f), DhLegendH = DcSE(16f);            // 44 · 6 · 26
    private static readonly float DhLvW = MathF.Floor((DhDetInW - 9f * DcSE(4f)) / 10f);                   // 59
    private static readonly float DhLvGapX = (DhDetInW - 10f * DhLvW) / 9f;                                // 6.9
    private static readonly float DhGridH = 2f * DhLvH + DhLvGap;                                           // 94
    // The block under the chips is ONE height in every state, so Solo / Match / Party never move (Detail.cs DhMidBlock):
    // a strut of DhMidH beside a top-aligned Fill Conditional — Master = grid · legend · tiles · caption, other = tiles ·
    // caption; the leftover sits under the caption. DhMidSlack is headroom for text that measures taller than its strut
    // (v4 sized the block to the exact sum, and the Master variant came out taller — the "list moves" bug, v5 §8).
    // Measured in game (v4, 2026-11-07 screenshots): the Master variant came out ~14 px over its paper sum; 26 px of
    // headroom keeps the strut the taller of the two with margin, so the window height never changes.
    private static readonly float DhMidSlack = DcSE(16f);                                                   // 26
    // Bureau Commissions room (2026-11-07, user-approved "a bit taller"): its six event rows + Accept bar + next-round
    // hint need a 410 px block (Commissions.cs DhCmBlockH) where the 932 px window left 343. Added HERE (the mid block)
    // so every tab grows by the same amount — the window stays one height and the dungeon panel's buttons stay at
    // its bottom; the list viewport (DhScrollH) and the activity filler grow with DhColsH. 74 = 67 needed + 7 slack.
    private static readonly float DhCmRoom = DcSE(46f);                                                     // 74
    private static readonly float DhMidH = DhGridH + DhLegendH + DhTileH + DhCapH + 3f * DhPanGap + DhMidSlack
                                         + DhCmRoom;                                                        // 344
    private static readonly float DhColsH = 2f * DhPanPadY + DhArtH + DhNameH + DhCapH + DhChipH + DhMidH
                                          + DhBtnH + 5f * DhPanGap;                                         // 770
    private static readonly float DhH = 24f + DhHeaderH + DhTabH + 2f * DhTabPadY + DhColsH + DhBarH + 3f * DhGap;  // 1006
    private static readonly float DhScrollH = DhColsH - 2f * DhPanPadY - DhCapH - DhPanGap;                // 706
    // Activity panel: art · name · sub-line ("This week: <boss>", 26 px strut for an 18 px line) · tiles · filler · caption · buttons.
    private static readonly float DhActSubH = DcSE(16f);                                                    // 26
    private static readonly float DhActFiller = DhColsH - 2f * DhPanPadY - DhArtH - DhNameH - DhActSubH - DhCapH - DhTileH
                                              - DhBtnH - 6f * DhPanGap;                                     // 248 - 26 - gap + 74
    private static readonly float DhCancelW = DcSE(76f), DhDot = DcSE(8f), DhCloseW = DcSE(28f);            // 122 · 12 · 44
    private static readonly int DhFsName = DcSi(12.5f), DhFsSub = DcSi(11f), DhFsTiny = DcSi(10.5f);        // 20 · 18 · 17
    private static readonly int DhFsTitle = DcSi(15f), DhFsSmall = DcSi(12f), DhFsBody = DcSi(13f);         // 24 · 19 · 21

    private static readonly ColorRgba DhGreen = Rgb(0x5fd38d), DhAmber = Rgb(0xffcc73), DhPurple = Rgb(0xb385ff);
    private static readonly ColorRgba DhBlue = Rgb(0x9cc8ff);   // Unstable (v4 mock .dc.u)
    private static readonly ColorRgba DhRed = Rgb(0xff8a8b);
    private static readonly ColorRgba DhSoloFill = Rgb(0x7aa2ff), DhSoloInk = Rgb(0x101c3a);     // v5 Solo button
    private static readonly ColorRgba DhPartyFill = Rgb(0xff8f7a), DhPartyInk = Rgb(0x3a140c);   // v5 Party button
    private const int DhListSlots = 16;   // v5: up to 6 Unstable entries ride on top of the dungeons

    // Top-align inside a row (WindowBuilder-Patterns.md "Top-align inside a row", ✅ in-game): an always-true Fill
    // Conditional takes the row's full height (flexibleHeight) and stretches its branch Column, whose own layout keeps
    // the child at the TOP at its preferred height.
    private static HudElement DhTopPinned(HudElement child) =>
        new ConditionalElement(() => true, new ColumnElement(new[] { child }, Gap: 0.01f), Fill: true);

    private IWindowControl DhRegister()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "dungeonhub.hub",
                Title:       "Dungeon Hub",
                DefaultRect: new WindowRect(0f, 0f, DhW, DhH),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = false, StartVisible = false, ShowTitleBar = false, Anchor = WindowAnchor.Center,
              ShouldRender = () => _dhShow && _services.ClientState.Phase == GamePhase.World &&
                                   (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildDhRoot(),
            OnClose: DhHide));
        _windows.Add(w);
        return w;
    }

    private HudElement BuildDhRoot() => new ColumnElement(new HudElement[]
    {
        DhHeader(),
        DhTabs(),
        // Both columns are pinned to the TOP of the row (DhTopPinned): a RowElement centres its children vertically
        // (UGuiPrimitives RowMode = MiddleLeft), so in v4 a detail panel that grew a few px in Master pushed the list
        // down by half of that. Now the list can't move whatever the right side measures.
        new RowElement(new HudElement[]
        {
            new CellElement(DhTopPinned(DhListPanel()), Width: DhListW),
            new CellElement(DhTopPinned(new ConditionalElement(() => DhIsGh(DhCur), DhGhPanel(),   // Guild Hunt: the dungeon look
                new ConditionalElement(() => DhIsCm(DhCur), DhCmPanel(),                         // Bureau Commissions (.Commissions.cs)
                new ConditionalElement(() => DhCur is { IsActivity: true }, DhActivityPanel(), DhDetailPanel())))), Width: DhDetW),
        }, Gap: DhGap),
        new ConditionalElement(() => _dhLive.Matching, DhBarMatching(), DhBarIdle()),
    }, Gap: DhGap);

    // ── header: accent tile + white gate · "Dungeon Hub" over the live status · icon-only ⚙ · icon-only ✕ ─────────────
    private HudElement DhHeader() => new RowElement(new HudElement[]
    {
        new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(DcSE(8f), DhHeaderH), new ImageElement(DhGateWhitePng, (int)DcSE(24f), (int)DcSE(24f)),
                new SpacerElement(DcSE(8f), DhHeaderH),
            }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Menu.MenuAccent, CornerRadius = DcS(9f), PaddingX = 0f, PaddingY = 0f, MaxWidth = DhHeaderH,
            }, Width: DhHeaderH),
        new CellElement(new ColumnElement(new HudElement[]
        {
            DcBold(() => "Dungeon Hub", TextColor, DhFsTitle),
            new RowElement(new HudElement[]
            {
                DcSmall(() => DhStatus().a, MutedColor, DhFsSmall),
                new ConditionalElement(() => DhStatus().b.Length > 0, DcWordSpace()),
                DcBold(() => DhStatus().b, () => Menu.MenuAccent, DhFsSmall),
                new ConditionalElement(() => DhStatus().c.Length > 0, DcWordSpace()),
                DcSmall(() => DhStatus().c, MutedColor, DhFsSmall),
            }, Gap: 0.01f),
        }, Gap: DcS(2f)), Weight: 1f),
        DhSettingsButton(),   // Dungeon Confirm settings (.DungeonHub.Settings.cs)
        DhCloseButton(),
    }, Gap: DcSE(10f));

    // Icon-only ✕ (house §4): no fill at rest, red wash on hover. The ✕ is a generated PNG (a BOLD "✕" draws blank —
    // Stellar-Design-Language.md §6), re-baked when its colour changes (DcGlyphPng caches per colour).
    private HudElement DhCloseButton()
    {
        var hover = new DcHover();
        float glyph = DcSE(12f), side = (DhCloseW - glyph) / 2f;
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(side, DhCloseW),
                new ImageElement(() => DcGlyphPng(DcGlyph.Cross, glyph, hover.On ? DhRed : MutedColor()), (int)glyph, (int)glyph),
                new SpacerElement(side, DhCloseW),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: DhHide) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => hover.On ? Over(Rgb(0xff4d4f, 0.22f), Menu.MenuBackground) : Transparent,
                CornerRadius = DhCloseW / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhCloseW,
            }, Width: DhCloseW);
    }

    // ── tab strip: five equal segments on a panel-coloured rounded bar; the current one = 25 % accent wash ──────────
    private HudElement DhTabs()
    {
        var tabs = new HudElement[DhTabCount];
        for (int i = 0; i < DhTabCount; i++)
        {
            int k = i;
            var hover = new DcHover();
            tabs[i] = new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, DhTabH),
                    DcDrop(DcBold(() => DhTabNames[k], () => _dhTab == k ? DcOn(Menu.MenuAccent, DhTabOnFill()) : MutedColor(),
                        DhFsSmall, DhTabW, TextAlign.Center)),
                }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: () => DhSelectTab(k)) { Bare = true, OnHover = o => hover.On = o })
                {
                    FillColorFn = () => _dhTab == k ? DhTabOnFill() : hover.On ? Over(Menu.MenuText with { A = 0.06f }, PanelColor()) : Transparent,
                    CornerRadius = DhTabH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhTabW,
                }, Width: DhTabW);
        }
        return new BubbleElement(new RowElement(tabs, Gap: DhTabGap))
        {
            FillColorFn = () => PanelColor(), CornerRadius = DhTabH / 2f + DhTabPadY - 1f, PaddingX = DhTabPadX, PaddingY = DhTabPadY,
        };
    }

    private ColorRgba DhTabOnFill() => DcWash(Menu.MenuAccent, 0.25f);

    // ── panel: caption (bold, muted) + right-aligned hint on a 28 px strut, then the children ─────────────────────
    private HudElement DhPanel(Func<string> caption, Func<string> hint, params HudElement[] children)
    {
        var items = new HudElement[children.Length + 1];
        items[0] = DhCaption(caption, hint);
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: DhPanGap))
        {
            FillColorFn = () => PanelColor(), CornerRadius = DhRadius, PaddingX = DhPanPadX, PaddingY = DhPanPadY,
        };
    }

    private HudElement DhCaption(Func<string> caption, Func<string>? hint = null) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, DhCapH),
        DcBold(caption, () => TextColor(), DhFsSmall),
        new SpacerElement(0f),   // flexible: pushes the hint to the right edge
        DcSmall(() => hint?.Invoke() ?? "", MutedColor, DhFsSmall),
    }, Gap: 0.01f);

    // ── list: a fixed pool of slots in a scroll viewport ──────────────────────────────────────────────────────────
    // v5: captions carry no step numbers ("DUNGEON", not "1 · DUNGEON").
    private HudElement DhListPanel()
    {
        var slots = new HudElement[DhListSlots];
        for (int i = 0; i < DhListSlots; i++) slots[i] = DhListSlot(i);
        return DhPanel(() => DhTab == DhTabHero ? "DUNGEON" : DhTab == DhTabRaid ? "RAID" : "ACTIVITY",
            () => DhCur?.CapText ?? "",
            new ScrollElement(new ListElement(() => Math.Min(DhListSlots, DhList.Count), slots), DhScrollH) { FixedHeight = true });
    }

    private int DhTab => _dhTab;

    // Group headers (v5 mock .grp): "OPEN TODAY" above the first Unstable entry, "DUNGEONS" above the first regular one
    // after them. No Unstable open today → no headers at all (nothing to tell apart). The ListElement has no header
    // rows, so a header is an optional line at the top of the slot of the entry it introduces.
    private string DhGroupHeader(int i)
    {
        if (_dhTab != DhTabHero || DhAt(i) is not { } e) return "";
        if (e.Unstable) return i == 0 ? "OPEN TODAY" : "";
        return i > 0 && DhAt(i - 1) is { Unstable: true } ? "DUNGEONS" : "";
    }

    private static readonly float DhGrpH = DcSE(14f), DhGrpPadX = DcS(2f);                                // 22 · 3
    private static readonly float DhUBarW = DcS(3f);                                                        // 5
    private static readonly float DhTagW = DcSE(62f), DhTagH = DcSE(14f), DhTagGap = DcS(4f);              // 100 · 22 · 6
    private static readonly int DhFsTag = DcSi(10f);                                                        // 16

    // One slot = [group header] · the row · 8 px (the slot stretches its child, Stellar-Design-Language R15). The row's
    // two looks are two WHOLE bubbles swapped by a Conditional — never a Conditional inside a bubble's measured child
    // (WindowBuilder-Patterns.md §BubbleElement).
    private HudElement DhListSlot(int i) => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => DhGroupHeader(i).Length > 0, new RowElement(new HudElement[]
        {
            new SpacerElement(DhGrpPadX, DhGrpH),
            DcBold(() => DhGroupHeader(i), MutedColor, DhFsTiny),
        }, Gap: 0.01f)),
        new ConditionalElement(() => DhAt(i) is { Unstable: true }, DhListRow(i, unstable: true), DhListRow(i, unstable: false)),
        new SpacerElement(0f, DhRowPitchPad),
    }, Gap: 0.01f);

    // One 84 px Tile row: art thumb · bold name over a muted sub-line. Selected = accent outline + a stronger accent
    // tint; locked = faded tile + text. Unstable (v5 mock .it.u): a light-blue left edge + an "UNSTABLE" tag before the
    // name.
    private HudElement DhListRow(int i, bool unstable)
    {
        float nameW = unstable ? DhRowTextW - DhTagW - DhTagGap : DhRowTextW;
        HudElement name = new TextElement(() => DhAt(i)?.Name ?? "", Color: () => DhAt(i) is { Locked: true } ? MutedColor() : TextColor(),
            Width: nameW, NoWrap: true, FontSize: DhFsName) { Bold = true, Ellipsize = true };
        if (unstable) name = new RowElement(new HudElement[] { DhUnstableTag(), new SpacerElement(DhTagGap, 0f), new CellElement(name, Width: nameW) }, Gap: 0.01f);
        HudElement lead = !unstable ? new SpacerElement(DhRowLead, DhRowH) : new RowElement(new HudElement[]
        {
            new CellElement(new BubbleElement(new SpacerElement(DhUBarW, DhRowH))
                { FillColorFn = () => DhBlue, CornerRadius = DcS(2f), PaddingX = 0f, PaddingY = 0f, MaxWidth = DhUBarW }, Width: DhUBarW),
            new SpacerElement(DhRowLead - DhUBarW, DhRowH),
        }, Gap: 0.01f);
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            lead,
            DhThumb(i),
            new SpacerElement(DhRowGapX, 0f),
            new CellElement(new ColumnElement(new HudElement[]
            {
                name,
                new TextElement(() => DhAt(i) is { } e ? DhRowSub(e) : "", Color: () => MutedColor(),   // muted even at 0 (info only)
                    NoWrap: true, FontSize: DhFsSub) { RichEmoji = true, Ellipsize = true },
            }, Gap: DcS(2f)), Width: DhRowTextW),
            new SpacerElement(DhRowGapX, 0f),
        }, Gap: 0.01f), OnClick: () => DhSelectEntry(i)) { Bare = true })
        {
            FillColorFn = () => DhAt(i) is not { } e ? DcTile()
                : e.Key == DhCur?.Key ? Over(Menu.MenuAccent with { A = 0.14f }, DcTile())
                : e.Locked ? Mix(PanelColor(), DcTile(), 0.5f) : DcTile(),
            OutlineColorFn = () => DhAt(i) is { } e && e.Key == DhCur?.Key ? Menu.MenuAccent : null,
            CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhRowW,
        };
    }

    // The small light-blue "UNSTABLE" tag (v5 mock .ub): 18 % blue wash, bold blue caps, fixed width.
    private HudElement DhUnstableTag() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhTagH),
            DcBold(() => "UNSTABLE", () => EnsureContrast(DhBlue, DcWash(DhBlue, 0.18f), 3f), DhFsTag, DhTagW, TextAlign.Center),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => DcWash(DhBlue, 0.18f), CornerRadius = DcS(7f), PaddingX = 0f, PaddingY = 0f, MaxWidth = DhTagW,
        }, Width: DhTagW);

    // ── bottom bar ────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement DhBar(params HudElement[] parts)
    {
        var items = new HudElement[parts.Length + 1];
        items[0] = new SpacerElement(0f, DhBarH);
        Array.Copy(parts, 0, items, 1, parts.Length);
        return new BubbleElement(new RowElement(items, Gap: DcSE(10f)))
        {
            FillColorFn = () => PanelColor(), CornerRadius = DhRadius, PaddingX = DcSE(8f), PaddingY = 0f,
        };
    }

    private HudElement DhBarDot(Func<ColorRgba> color) => new CellElement(new BubbleElement(new SpacerElement(DhDot, DhDot))
    {
        FillColorFn = () => color(), CornerRadius = DhDot / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhDot,
    }, Width: DhDot);

    private HudElement DhBarIdle() => DhBar(
        DhBarDot(MutedColor),
        DcSmall(DhIdleText, DhIdleColor, DhFsSmall),
        new SpacerElement(0f),
        DcSmall(DhPartyText, MutedColor, DhFsSmall),
        new SpacerElement(DcSE(2f), 0f));

    // Matching: accent dot · bold "Matching" · muted "· <dungeon> (<difficulty>) · mm:ss" · red Cancel (any tab).
    private HudElement DhBarMatching() => DhBar(
        DhBarDot(() => Menu.MenuAccent),
        DcBold(() => "Matching", TextColor, DhFsSmall),
        new CellElement(new TextElement(DhMatchText, Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSmall)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
        DhPill(() => "Cancel", () => DangerFill, () => DcOn(White, Over(DangerFill, PanelColor())), DhOnCancel, DhCancelW, DhChipH));

    // A round pill of a fixed size: bold centred label, hover = 10 % text wash (R2: the hover follows the shape).
    private HudElement DhPill(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick, float w, float h)
    {
        var hover = new DcHover();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, h), DcDrop(DcBold(label, text, DhFsSmall, w, TextAlign.Center)),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: onClick) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => hover.On ? Over(Menu.MenuText with { A = 0.10f }, fill()) : fill(),
                CornerRadius = h / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            }, Width: w);
    }
}
