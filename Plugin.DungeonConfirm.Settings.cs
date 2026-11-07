using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Hub settings window (opened by the hub header's gear, .DungeonHub.Settings.cs) — the Stellar house look,
// user-approved 2026-11-07. Borderless like the hub (ShowTitleBar = false: the whole frame drags, our own ✕ closes),
// 480 mock px × DcScale like every other Dungeon Hub window:
//   ToolHeader   [accent tile + white gear] "Dungeon Hub Settings" / "Confirm screen On|Off" … [✕]
//   CONFIRM SCREEN  replace row + chip · [Preview 5-player] [Preview raid]
//   SOUNDS          open-sound row + chip · Pill dropdown + ▶ · tick row + chip · dropdown + ▶ · Volume slider ·
//                   [Open sounds folder] + note. A chosen file that can't be played → yellow note under its dropdown.
//   WINDOW          custom window opacity row + chip · Opacity slider while on (.WindowOpacity.cs)
// Toolkit notes: the framework Pill dropdown is drawn at its own fixed 28 px (it doesn't take our ×1.6 scale), and its
// trigger corners are the shared small rounding until the SDK has DropdownElement.CornerRadius (fw ≥ 2.29.0).
// Sub-lines wrap because they sit in FIXED-width cells (a weighted cell in a Row doesn't give wrapping text a width).
// ⚠️ Every size here is built only from DcS / DcSE (static METHODS) — see the scale note in .DungeonConfirm.Window.cs.
public sealed partial class Plugin
{
    private static readonly float DsW = DcSE(480f);                                               // 768
    private static readonly float DsInW = DcSE(480f) - 24f - 2f * DcSE(10f);                       // 712 (panel inner)
    private static readonly float DsGap = DcSE(8f), DsChipW = DcSE(64f), DsChipH = DcSE(24f);       // 12 · 102 · 38
    private static readonly float DsPillH = DcSE(28f), DsPlayW = DcSE(28f);                        // 44 · 44
    private static readonly float DsLabelW = DsInW - DcSE(64f) - DcSE(8f);                         // 598
    private static readonly float DsHalfW = (DsInW - DcSE(8f)) / 2f;                               // 350
    private static readonly float DsDropW = DsInW - DcSE(28f) - DcSE(8f);                          // 656
    private static readonly float DsVolLabelW = DcSE(56f), DsVolValW = DcSE(40f);                  // 90 · 64
    private static readonly float DsFolderW = DcSE(150f), DsFolderNoteW = DsInW - DcSE(150f) - DcSE(8f);   // 240 · 460

    private IWindowControl DcRegisterSettings()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "dungeonhub.confirm.settings",
                Title:       "Dungeon Hub Settings",
                // A fixed literal (2560×1440 reference canvas), never ScreenWidth-derived (WindowBuilder-Patterns.md
                // §"DefaultRect must be a CONSTANT"); every open re-centres it on the hub anyway.
                DefaultRect: new WindowRect(2540f - DsW, 20f, DsW, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = false, StartVisible = false, ShowTitleBar = false,
              FrameOpacity = DhFrameOpacity,   // settings' custom opacity, null = theme (.WindowOpacity.cs)
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World &&
                                   (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildDcSettingsRoot(),
            OnClose: DhHideSettings));
        _windows.Add(w);
        return w;
    }

    private HudElement BuildDcSettingsRoot() => new ColumnElement(new HudElement[]
    {
        DsHeader(),
        DcPanel(() => "CONFIRM SCREEN", new SpacerElement(0f, 0f),
            DsToggleRow("Replace the game's confirm screen",
                "A small window instead of the full-screen entry check, for dungeons, raids and match found.",
                () => _dcReplace, () => ApplyDcReplace(!_dcReplace)),
            new RowElement(new HudElement[]
            {
                DsNeutralPill("Preview 5-player", () => DcStartPreview(false), DsHalfW),
                DsNeutralPill("Preview raid",     () => DcStartPreview(true),  DsHalfW),
            }, Gap: DsGap)),
        DcPanel(() => "SOUNDS", new SpacerElement(0f, 0f),
            DsToggleRow("Sound when it opens", null, () => _dcSoundOpen,
                () => SetDcSoundBool("dungeon_confirm_sound_open", !_dcSoundOpen, x => _dcSoundOpen = x)),
            DsSoundPicker(tick: false),
            DsToggleRow("Countdown ticks", "Each second for the last 10 s while you still have to answer", () => _dcSoundTicks,
                () => SetDcSoundBool("dungeon_confirm_sound_ticks", !_dcSoundTicks, x => { _dcSoundTicks = x; if (!x) DcSoundStopTicks(); })),
            DsSoundPicker(tick: true),
            DsVolumeRow(),
            new RowElement(new HudElement[]
            {
                DsNeutralPill("Open sounds folder", DcOpenSoundsFolder, DsFolderW),
                new CellElement(DsWrapNote("Put your own .wav or .mp3 files here, then pick them above."), Width: DsFolderNoteW),
            }, Gap: DsGap)),
        DsWindowPanel(),   // WINDOW: custom opacity (.WindowOpacity.cs)
    }, Gap: DhGap);

    // ── ToolHeader: accent tile + white gear · title over "Confirm screen On/Off" · icon-only ✕ ───────────────────
    private HudElement DsHeader() => new RowElement(new HudElement[]
    {
        new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(DcSE(8f), DhHeaderH),
                new ImageElement(() => DcGlyphPng(DcGlyph.Gear, DcSE(24f), White), (int)DcSE(24f), (int)DcSE(24f)),
                new SpacerElement(DcSE(8f), DhHeaderH),
            }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fade(Menu.MenuAccent), CornerRadius = DcS(9f), PaddingX = 0f, PaddingY = 0f, MaxWidth = DhHeaderH,
            }, Width: DhHeaderH),
        new CellElement(new ColumnElement(new HudElement[]
        {
            DcBold(() => "Dungeon Hub Settings", TextColor, DhFsTitle),
            new RowElement(new HudElement[]
            {
                DcSmall(() => "Confirm screen", MutedColor, DhFsSmall),
                DcWordSpace(),   // edge spaces in a run are dropped (.DungeonConfirm.Window.cs header note)
                DcBold(() => _dcReplace ? "On" : "Off", () => Menu.MenuAccent, DhFsSmall),
            }, Gap: 0.01f),
        }, Gap: DcS(2f)), Weight: 1f),
        DhCloseButton(DhHideSettings),
    }, Gap: DcSE(10f));

    // ── rows ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // label (+ muted wrapping sub-line) in a fixed cell · the ON/OFF chip pinned right.
    private HudElement DsToggleRow(string label, string? sub, Func<bool> on, Action toggle)
    {
        HudElement text = DcSmall(() => label, TextColor, DcFsBody);
        if (sub != null) text = new ColumnElement(new HudElement[] { text, DsWrapNote(sub) }, Gap: DcS(2f));
        return new RowElement(new HudElement[] { new CellElement(text, Width: DsLabelW), DsChip(on, toggle) }, Gap: DsGap);
    }

    private TextElement DsWrapNote(string text) =>
        new(() => text, Color: () => MutedColor(), FontSize: DcFsSmall) { RichEmoji = true };

    // Pill dropdown (Default + the folder's files) · ▶ preview; under it the yellow "can't play" note when needed.
    private HudElement DsSoundPicker(bool tick) => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(new DropdownElement(() => DcSoundSelected(tick), () => DcSoundOptions(tick), i => DcSoundPick(tick, i),
                Width: DsDropW) { Style = DropdownStyle.Pill, MaxVisibleRows = 8 }, Width: DsDropW),
            DsPlayButton(() => DcSoundPreview(tick)),
        }, Gap: DsGap),
        new ConditionalElement(() => DcSoundProblem(tick).Length > 0,
            new CellElement(new TextElement(() => $"Can't play {DcSoundProblem(tick)} · using the default sound",
                Color: () => DcAmber, NoWrap: true, FontSize: DcFsSmall) { RichEmoji = true, Ellipsize = true }, Width: DsInW)),
    }, Gap: DcS(4f));

    private HudElement DsVolumeRow() => new RowElement(new HudElement[]
    {
        new CellElement(DcSmall(() => "Volume", MutedColor, DcFsBody), Width: DsVolLabelW),
        new CellElement(new SliderElement(() => _dcSoundVolume, SetDcSoundVolume, 0f, 100f)
            { SquareHandle = true, HandleSize = DcSE(14f) }, Weight: 1f),
        new CellElement(DcBold(() => $"{_dcSoundVolume}%", TextColor, DcFsSmall, DsVolValW, TextAlign.Right), Width: DsVolValW),
    }, Gap: DsGap);

    // ── controls ──────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement DsNeutralPill(string label, Action onClick, float w) =>
        DhPill(() => label, DcNeutral, () => EnsureContrast(TextColor(), DcNeutral(), 4.5f), onClick, w, DsPillH);

    // ON/OFF chip, 102×38 in both states (house "clickable on/off chip", MinimalNameplate recipe): two COMPLETE chips
    // swapped by a Conditional in one fixed cell, sharing one hover flag. On = bold accent on a 22 % accent wash, off =
    // muted on the neutral fill.
    private HudElement DsChip(Func<bool> on, Action toggle)
    {
        var hover = new DcHover();
        Func<ColorRgba> onFill = () => DcWash(Menu.MenuAccent, 0.22f);
        return new CellElement(new ConditionalElement(on,
            DsChipShell(DcDrop(DcBold(() => "ON", () => EnsureContrast(Menu.MenuAccent, onFill(), 4.5f), DcFsSmall, DsChipW, TextAlign.Center)),
                onFill, hover, toggle),
            DsChipShell(DcSmall(() => "OFF", MutedColor, DcFsSmall, DsChipW, TextAlign.Center), DcNeutral, hover, toggle)),
            Width: DsChipW);
    }

    private HudElement DsChipShell(HudElement label, Func<ColorRgba> fill, DcHover hover, Action onClick) =>
        new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DsChipH), label,
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: onClick) { Bare = true, OnHover = o => hover.On = o })
        {
            FillColorFn = () => Fade(hover.On ? Over(Menu.MenuText with { A = 0.10f }, fill()) : fill()),
            CornerRadius = DsChipH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DsChipW,
        };

    // Round neutral ▶ (generated PNG, DcGlyph.Play), 44×44, hover = 10 % text wash.
    private HudElement DsPlayButton(Action onClick)
    {
        var hover = new DcHover();
        float glyph = DcSE(12f), side = (DsPlayW - glyph) / 2f;
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(side, DsPlayW),
                new ImageElement(() => DcGlyphPng(DcGlyph.Play, glyph, EnsureContrast(TextColor(), DcNeutral(), 4.5f)), (int)glyph, (int)glyph),
                new SpacerElement(side, DsPlayW),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: onClick) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(hover.On ? Over(Menu.MenuText with { A = 0.10f }, DcNeutral()) : DcNeutral()),
                CornerRadius = DsPlayW / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DsPlayW,
            }, Width: DsPlayW);
    }
}
