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
//                   ▶ = play/stop toggle: ■ on the accent while that row's preview plays (DsPlayButton, .Sound.cs) ·
//                   [Open sounds folder] + note. A chosen file that can't be played → yellow note under its dropdown.
//                   Replace OFF → the whole section is inert + faded, with a "why" line under the caption (DsSoundsOn).
//   WINDOW          custom window opacity row + chip · Opacity slider while on (.WindowOpacity.cs)
// Toolkit notes: the framework Pill dropdown is drawn at its own fixed 28 px (it doesn't take our ×1.6 scale), so the
// ▶ next to it is a plain 28 px too (not DcSE): a round control is exactly its side, and the pair must share it.
// The trigger's capsule corners need DropdownElement.CornerRadius (fw ≥ 2.29.0; the framework caps it at 28/2 − 1 = 13).
// Sub-lines wrap because they sit in FIXED-width cells (a weighted cell in a Row doesn't give wrapping text a width).
// ⚠️ Every size here is built only from DcS / DcSE (static METHODS) — see the scale note in .DungeonConfirm.Window.cs.
public sealed partial class Plugin
{
    private static readonly float DsW = DcSE(480f);                                               // 768
    private static readonly float DsInW = DcSE(480f) - 24f - 2f * DcSE(10f);                       // 712 (panel inner)
    private static readonly float DsGap = DcSE(8f), DsChipW = DcSE(64f), DsChipH = DcSE(24f);       // 12 · 102 · 38
    private static readonly float DsPillH = DcSE(28f);                                             // 44
    // The sound row: framework Pill dropdown (fixed, unscaled 28 px) + a ▶ of the SAME side, both radius side/2 − 1.
    private const float DsDropH = 28f, DsPlayW = DsDropH, DsDropRadius = DsDropH / 2f - 1f;         // 28 · 28 · 13
    private static readonly float DsLabelW = DsInW - DcSE(64f) - DcSE(8f);                         // 598
    private static readonly float DsHalfW = (DsInW - DcSE(8f)) / 2f;                               // 350
    private static readonly float DsDropW = DsInW - DsPlayW - DsGap;                               // 672
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
              TitleProvider = () => L("dc.set.title"),
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
        DcPanel(() => L("dc.set.cap.confirm"), new SpacerElement(0f, 0f),
            DsToggleRow("dc.set.replace", "dc.set.replace.sub",
                () => _dcReplace, () => ApplyDcReplace(!_dcReplace)),
            new RowElement(new HudElement[]
            {
                DsNeutralPill("dc.set.preview5",    () => DcStartPreview(false), DsHalfW),
                DsNeutralPill("dc.set.previewRaid", () => DcStartPreview(true),  DsHalfW),
            }, Gap: DsGap)),
        DcPanel(() => L("dc.set.cap.sounds"), new SpacerElement(0f, 0f),
            // Replace OFF → the "why" line first. A Then/Else pair (the note + row, or the row alone), not a lone
            // Conditional: a hidden Cond still takes a panel gap, which would shift the section while Replace is on.
            new ConditionalElement(() => !_dcReplace,
                new ColumnElement(new HudElement[]
                {
                    new CellElement(DsWrapNote("dc.set.soundsOff"), Width: DsInW),
                    DsOpenSoundRow(),
                }, Gap: DcPanelGap),
                DsOpenSoundRow()),
            DsSoundPicker(tick: false),
            DsToggleRow("dc.set.ticks", "dc.set.ticks.sub", () => _dcSoundTicks,
                () => SetDcSoundBool("dungeon_confirm_sound_ticks", !_dcSoundTicks, x => { _dcSoundTicks = x; if (!x) DcSoundStopTicks(); }),
                DsSoundsOn),
            DsSoundPicker(tick: true),
            DsVolumeRow(),
            new RowElement(new HudElement[]
            {
                DsNeutralPill("dc.set.folder", DcOpenSoundsFolder, DsFolderW, DsSoundsOn),
                new CellElement(DsWrapNote("dc.set.folder.note"), Width: DsFolderNoteW),
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
            DcBold(() => L("dc.set.title"), TextColor, DhFsTitle),
            new RowElement(new HudElement[]
            {
                DcSmall(() => L("dc.set.confirmScreen"), MutedColor, DhFsSmall),
                DcWordSpace(),   // edge spaces in a run are dropped (.DungeonConfirm.Window.cs header note)
                // status ink like the chips: On = house status green (contrast-lifted on light themes), Off = muted, never accent
                DcBold(() => L(_dcReplace ? "dc.set.on" : "dc.set.off"),
                    () => _dcReplace ? EnsureContrast(DhGreen, PanelColor(), 4.5f) : MutedColor(), DhFsSmall),
            }, Gap: 0.01f),
        }, Gap: DcS(2f)), Weight: 1f),
        DhCloseButton(DhHideSettings),
    }, Gap: DcSE(10f));

    // ── SOUNDS disabled while Replace is OFF ──────────────────────────────────────────────────────────────────────
    // The sounds only ever play from the replacement window (and its previews), so with Replace OFF every SOUNDS
    // control is inert and drawn in the plugin's disabled look — the locked Solo/Match/Party pill fade
    // (.DungeonHub.Detail.cs DhGoButton): fill 55 % of the way from the panel, ink 40 % text-over-panel. The saved
    // settings are never touched; Replace back ON brings everything back as it was. Same geometry in both states:
    // chips / pills / ▶ keep their shell and only gate click + hover; the dropdown (no disabled flag in the SDK) is
    // swapped for a static look-alike; the slider uses SliderElement.Enabled (Unity interactable).
    private bool DsSoundsOn() => _dcReplace;
    private ColorRgba DsOffFill(ColorRgba fill) => Mix(PanelColor(), fill, 0.55f);
    private ColorRgba DsOffInk() => Mix(PanelColor(), TextColor(), 0.4f);
    private Func<ColorRgba> DsLabelInk(Func<bool>? enabled) => () => enabled == null || enabled() ? TextColor() : MutedColor();

    private HudElement DsOpenSoundRow() => DsToggleRow("dc.set.openSound", null, () => _dcSoundOpen,
        () => SetDcSoundBool("dungeon_confirm_sound_open", !_dcSoundOpen, x => _dcSoundOpen = x), DsSoundsOn);

    // ── rows ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // labelKey (+ muted wrapping subKey line, catalog keys) in a fixed cell · the ON/OFF chip pinned right. enabled null = always live.
    private HudElement DsToggleRow(string labelKey, string? subKey, Func<bool> on, Action toggle, Func<bool>? enabled = null)
    {
        HudElement text = DcSmall(() => L(labelKey), DsLabelInk(enabled), DcFsBody);
        if (subKey != null) text = new ColumnElement(new HudElement[] { text, DsWrapNote(subKey) }, Gap: DcS(2f));
        return new RowElement(new HudElement[] { new CellElement(text, Width: DsLabelW), DsChip(on, toggle, enabled) }, Gap: DsGap);
    }

    private TextElement DsWrapNote(string key) =>
        new(() => L(key), Color: () => MutedColor(), FontSize: DcFsSmall) { RichEmoji = true };

    // Pill dropdown (Default + the folder's files) · ▶ preview; under it the yellow "can't play" note when needed.
    private HudElement DsSoundPicker(bool tick) => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(new ConditionalElement(DsSoundsOn,
                new DropdownElement(() => DcSoundSelected(tick), () => DcSoundOptions(tick), i => DcSoundPick(tick, i),
                    Width: DsDropW) { Style = DropdownStyle.Pill, CornerRadius = DsDropRadius, MaxVisibleRows = 8 },
                DsDropdownOff(tick)), Width: DsDropW),
            DsPlayButton(() => DcSoundPreview(tick), DsSoundsOn, () => DcPreviewPlaying(tick)),
        }, Gap: DsGap),
        new ConditionalElement(() => DcSoundProblem(tick).Length > 0,
            new CellElement(new TextElement(() => LF("dc.set.cantPlay", DcSoundProblem(tick)),
                Color: () => DcAmber, NoWrap: true, FontSize: DcFsSmall) { RichEmoji = true, Ellipsize = true }, Width: DsInW)),
    }, Gap: DcS(4f));

    private HudElement DsVolumeRow() => new RowElement(new HudElement[]
    {
        new CellElement(DcSmall(() => L("dc.set.volume"), () => DsSoundsOn() ? MutedColor() : DsOffInk(), DcFsBody, DsVolLabelW), Width: DsVolLabelW),
        // Enabled = Unity Slider.interactable (framework SliderBinding): no drag while Replace is OFF, same geometry. The
        // SDK can't recolour the track fill, so only the knob takes Unity's disabled tint; the label + value go muted.
        new CellElement(new SliderElement(() => _dcSoundVolume, SetDcSoundVolume, 0f, 100f, DsSoundsOn)
            { SquareHandle = true, HandleSize = DcSE(14f) }, Weight: 1f),
        new CellElement(DcBold(() => $"{_dcSoundVolume}%", DsLabelInk(DsSoundsOn), DcFsSmall, DsVolValW, TextAlign.Right), Width: DsVolValW),
    }, Gap: DsGap);

    // Static stand-in for the Pill dropdown trigger while SOUNDS is disabled: the framework trigger's own composition
    // (WindowBuilder.Dropdown.Pill.cs — 12 px insets, bold 13 caption, 6 px gap, 8×5 ▾, capsule radius 13, 28 tall),
    // no Selectable, so nothing opens. The caption still names the saved choice; ▾ is our generated image (no TMP ▾).
    private HudElement DsDropdownOff(bool tick)
    {
        const float padX = 12f, chevGap = 6f, chevW = 8f;
        Func<string> caption = () => { var o = DcSoundOptions(tick); int i = DcSoundSelected(tick); return i >= 0 && i < o.Count ? o[i] : ""; };
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(padX, DsDropH),
            new CellElement(new RowElement(new HudElement[]
            {
                new TextElement(caption, Color: () => DsOffInk(), NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true },
                new SpacerElement(chevGap, 0f),
                new ImageElement(() => DcGlyphPng(DcGlyph.Chevron, chevW, DsOffInk()), (int)chevW, (int)chevW),
            }, Gap: 0.01f, Justify: RowJustify.Center), Width: DsDropW - 2f * padX),
            new SpacerElement(padX, DsDropH),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(DsOffFill(DcNeutral())), CornerRadius = DsDropRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DsDropW,
        };
    }

    // ── controls ──────────────────────────────────────────────────────────────────────────────────────────────────
    // enabled null = always live. Disabled = same pill, click + hover gated, disabled fill + ink.
    private HudElement DsNeutralPill(string labelKey, Action onClick, float w, Func<bool>? enabled = null)
    {
        if (enabled == null)
            return DhPill(() => L(labelKey), DcNeutral, () => EnsureContrast(TextColor(), DcNeutral(), 4.5f), onClick, w, DsPillH);
        var hover = new DcHover();
        Func<ColorRgba> fill = () => enabled() ? DcNeutral() : DsOffFill(DcNeutral());
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DsPillH),
                DcDrop(DcBold(() => L(labelKey), () => enabled() ? EnsureContrast(TextColor(), DcNeutral(), 4.5f) : DsOffInk(),
                    DhFsSmall, w, TextAlign.Center)),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: () => { if (enabled()) onClick(); })
            { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(hover.On && enabled() ? Over(Menu.MenuText with { A = 0.10f }, fill()) : fill()),
                CornerRadius = DsPillH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            }, Width: w);
    }

    // ON/OFF chip, 102×38 in both states (house "clickable on/off chip", MinimalNameplate recipe): two COMPLETE chips
    // swapped by a Conditional in one fixed cell, sharing one hover flag. On = bold status green #5fd38d on a 22 % green
    // wash (NOT the theme accent — a pink accent read as "warning", not "on"), off = muted on the neutral fill.
    // Disabled (enabled() false): still shows the saved ON/OFF, in the disabled fade; click + hover do nothing.
    private HudElement DsChip(Func<bool> on, Action toggle, Func<bool>? enabled = null)
    {
        var hover = new DcHover();
        Func<bool> live = enabled ?? (() => true);
        Func<ColorRgba> onFill = () => live() ? DcWash(DhGreen, 0.22f) : DsOffFill(DcWash(DhGreen, 0.22f));
        Func<ColorRgba> offFill = () => live() ? DcNeutral() : DsOffFill(DcNeutral());
        Action click = () => { if (live()) toggle(); };
        return new CellElement(new ConditionalElement(on,
            DsChipShell(DcDrop(DcBold(() => L("dc.set.chipOn"), () => live() ? EnsureContrast(DhGreen, onFill(), 4.5f) : DsOffInk(),
                DcFsSmall, DsChipW, TextAlign.Center)), onFill, hover, click, live),
            DsChipShell(DcSmall(() => L("dc.set.chipOff"), () => live() ? MutedColor() : DsOffInk(), DcFsSmall, DsChipW, TextAlign.Center),
                offFill, hover, click, live)),
            Width: DsChipW);
    }

    private HudElement DsChipShell(HudElement label, Func<ColorRgba> fill, DcHover hover, Action onClick, Func<bool> live) =>
        new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DsChipH), label,
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: onClick) { Bare = true, OnHover = o => hover.On = o })
        {
            FillColorFn = () => Fade(hover.On && live() ? Over(Menu.MenuText with { A = 0.10f }, fill()) : fill()),
            CornerRadius = DsChipH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DsChipW,
        };

    // Round neutral ▶ (generated PNG, DcGlyph.Play), 28×28 = the Pill dropdown beside it, hover = 10 % text wash. The
    // glyph keeps the mock's 12-in-28 proportion, so it is unscaled too. Disabled: click + hover gated, faded fill, and
    // the glyph swapped (Conditional, same 12×12) for a disabled-ink copy — an ImageElement's PNG is baked once at build.
    // Playing (that row's preview, incl. its async load — DcPreviewPlaying, user-approved "Option A" 2026-11-13): the
    // same shell on the Primary accent with a white ■ (DcGlyph.Stop, 3.0 Primary contrast rule), hover = 10 % white;
    // click stops it. Only fill + glyph change, so nothing moves between the states.
    private HudElement DsPlayButton(Action onClick, Func<bool> enabled, Func<bool> playing)
    {
        var hover = new DcHover();
        float glyph = 12f, side = (DsPlayW - glyph) / 2f;
        Func<ColorRgba> fill = () => !enabled() ? DsOffFill(DcNeutral()) : playing() ? Menu.MenuAccent : DcNeutral();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(side, DsPlayW),
                new ConditionalElement(enabled,
                    new ConditionalElement(playing,
                        new ImageElement(() => DcGlyphPng(DcGlyph.Stop, glyph, EnsureContrast(White, Menu.MenuAccent, 3.0f)), (int)glyph, (int)glyph),
                        new ImageElement(() => DcGlyphPng(DcGlyph.Play, glyph, EnsureContrast(TextColor(), DcNeutral(), 4.5f)), (int)glyph, (int)glyph)),
                    new ImageElement(() => DcGlyphPng(DcGlyph.Play, glyph, DsOffInk()), (int)glyph, (int)glyph)),
                new SpacerElement(side, DsPlayW),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: () => { if (enabled()) onClick(); })
            { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(!hover.On || !enabled() ? fill()
                    : Over((playing() ? White : Menu.MenuText) with { A = 0.10f }, fill())),
                CornerRadius = DsPlayW / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DsPlayW,
            }, Width: DsPlayW);
    }
}
