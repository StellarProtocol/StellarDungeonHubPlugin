using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm windows — the approved prototype v3, built from house-style blocks (Stellar-Design-Language.md).
// Two registered windows (5-player and raid), one fixed layout each, so neither ever grows or shrinks: every
// row/caption/header is pinned by a strut and the leftover height goes into a filler at the bottom of the PARTY / RAID
// panel (the prototype's flex:1 panel). Body padding is the framework's fixed GlassMenu 12 on all sides (the prototype
// had 10 top), so content height = window height − 24.
//
// Toolkit notes behind the choices:
//   • Plain TextElement on a Menu window is the fixed 14 px legacy face. The TMP path is the only one that honours
//     FontSize, and RichEmoji = true is the way to reach it WITHOUT bold (it is the TMP regular face with the chat
//     sprite asset, no sprite tags here) → DcSmall.
//   • Pills are round (R1: radius = side/2 − 1), Bare selectables with the hover drawn by the bubble (R2) — the newer
//     Maestro recipe; Plugin.Style.cs's square-cornered PillButton is left alone for Homestead.
//   • · … render in the TMP faces (TmpStyledText.Ellipsis: both shipped TMP faces carry U+2026). ✓ ✕ ★ are NOT
//     trusted in BOLD (the header's bold ✕ drew blank in game, 2026-11-06), so every one is a generated PNG (Art.cs).
//   • Text runs side by side lose their edge spaces (a TMP run's preferred width drops trailing whitespace: "Leader " +
//     name read "LeaderPlayer"), so runs carry no edge spaces and the word gap is an explicit DcWordGap spacer.
public sealed partial class Plugin
{
    // ── scale ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The prototype was drawn at 1× (345 px wide). On the 2560×1440 reference canvas that read as tiny in game, so
    // EVERY size below is the prototype value × DcScale (user, 2026-11-06). DcS = rounded (vertical gaps, paddings,
    // fonts, radii); DcSE = rounded to EVEN (box widths/heights, horizontal spacers/gaps) — WindowBuilder-Patterns.md
    // §"Sub-pixel blur": an odd row height or width centres its children on a half pixel.
    // ⚠️ All scaled sizes are static readonly and live HERE, in dependency order: static initialisers run in textual
    // order within one file, but the order ACROSS partial files is unspecified (a field in another file could read 0).
    private const float DcScale = 1.6f;
    private static float DcS(float x)  => MathF.Round(x * DcScale);
    private static float DcSE(float x) => 2f * MathF.Round(x * DcScale / 2f);
    private static int   DcSi(float x) => (int)DcS(x);

    private const float DcPad = 24f;   // framework GlassMenu body padding (12 per side) — fixed, NOT scaled
    // 5-player width: 345 × 1.6 = 552 cut an 11-glyph name + "YOU ASSIST" to 7 glyphs + "…" in game (2026-11-06), so it is 680
    // (425 × 1.6); the extra 128 goes to the flexible name cell and header title. Height unchanged; art strip just wider.
    private static readonly float Dc5W = DcSE(425f), DcRW = DcSE(640f);                                    // 680 / 1024
    private static readonly float DcHeaderH = DcSE(40f), DcBarH = DcSE(6f), DcCaptionH = DcSE(18f);        // 64 · 10 · 28
    private static readonly float DcPillW = DcSE(116f), DcPillH = DcSE(28f);                               // 186 × 44
    // Maestro's pill recipe × DcScale: PillPadX 12 → 20 per side (label box 146; "Cancel Entry" bold 21 ≈ 129, measured
    // 80 at bold 13 in-game), BoldLabelDrop 1 → 2 (Plugin.Style.cs DropBold; full-spec R9).
    private static readonly float DcPillPadX = DcSE(12f), DcBoldDrop = DcS(1f);                            // 20 · 2
    private static readonly float DcRowH = DcSE(26f), DcRowGap = DcS(2f);                                  // 42 · 3
    private static readonly float DcStatusW = DcSE(76f), DcStatusH = DcSE(20f);                            // 122 × 32
    // Difficulty chip (header, after the title): 32 high (radius 15), 132 wide = the longest label ("Nightmare" /
    // "Master 20", 9 glyphs ≈ 101 at bold 19 — DcFit's 7 px × DcScale estimate) + 2 × 14 padding, rounded up to even.
    private static readonly float DcDiffW = DcSE(82f), DcDiffPadX = DcS(9f);                               // 132 · 14
    private static readonly float DcGap = DcS(8f), DcPanelGap = DcS(6f), DcRadius = DcS(6f);               // 13 · 10 · 10
    private static readonly float DcPanelPadX = DcSE(10f), DcPanelPadY = DcS(8f);                          // 16 · 13
    private static readonly float DcIconBox = DcSE(40f), DcIconRadius = DcS(9f), DcSwordsW = DcSE(24f);    // 64 · 14 · 38
    private static readonly float DcClassW = DcSE(20f), DcStarW = DcSE(12f);                               // 32 · 20
    private static readonly float DcClassIconW = DcSE(15f), DcBadgeRadius = MathF.Round(DcClassW * 0.22f); // 24 · 7
    private static readonly float DcChipH = DcSE(24f), DcAffixIconW = DcSE(18f);                           // 38 · 28
    private static readonly float DcSp4 = DcSE(4f), DcSp6 = DcSE(6f), DcSp7 = DcSE(7f), DcSp8 = DcSE(8f);   // 6 · 10 · 12 · 12
    private static readonly float DcSp10 = DcSE(10f), DcTimerW = DcSE(52f);                                // 16 · 84
    private static readonly float DcAssistW = DcSE(40f);                                                   // 64
    // Ability score column (right-aligned, muted), "AS (IBS)": "666,666 (6,666)" regular 19 ≈ 10 digits × 10.5 + 2 commas
    // × 5 + space 5 + brackets 2 × 6 ≈ 132 → 140; the raid row's smaller copy (tag 16) ≈ 132 × 16/19 ≈ 111 → 128.
    private static readonly float DcScoreW = DcSE(88f), DcScoreRaidW = DcSE(80f);                          // 140 · 128
    private static readonly int   Dc5ArtH = (int)DcSE(122f), DcRArtH = (int)DcSE(156f);                    // 196 · 250
    // Fonts: title 24 · timer 29 · body (names, pill labels) 21 · small (captions, status, chips) 19 · mark 18 · tag 16.
    // Generated ✓/✕ images (Art.cs DcGlyphPng): 16 in a raid mark or a body-size pill label.
    private static readonly int DcFsTitle = DcSi(15f), DcFsTimer = DcSi(18f), DcFsBody = DcSi(13f), DcFsSmall = DcSi(12f);
    private static readonly int DcFsMark = DcSi(11f), DcFsTag = DcSi(10f);
    private static readonly float DcGlyphLW = DcSE(10f), DcWordGap = DcS(3f);         // 16 · 5

    // Heights. 5-player content = art 196 + header 64 + bar 10 + affix 102 + party 389 + footer 44 + 5 gaps × 13 = 870.
    // A panel = padY 13 + caption 28 + panel gap 10 + children (each after a 10 gap) + padY 13. Party = 64 + rows 222 +
    // 10 + filler 93 = 389. Without affixes the 102 panel + its 13 gap go to the party filler (208), so the window stays
    // fixed. Window = content + 24 = 894.
    private static readonly float DcRowsH  = 5f * DcRowH + 4f * DcRowGap;                                   // 222
    private static readonly float DcPanelChrome = 2f * DcPanelPadY + DcCaptionH + DcPanelGap;               // 64
    private static readonly float DcAffixH = DcPanelChrome + DcChipH;                                       // 102
    private static readonly float Dc5Filler = DcS(58f), Dc5FillerNoAffix = Dc5Filler + DcAffixH + DcGap;   // 93 / 208
    private static readonly float Dc5H = DcPad + Dc5ArtH + DcHeaderH + DcBarH + DcAffixH
                                       + DcPanelChrome + DcRowsH + DcPanelGap + Dc5Filler + DcPillH + 5f * DcGap;   // 894
    // Raid: group = caption 28 + 6 + rows 222 = 256; grid = 2 × 256 + 10 = 522; panel = 64 + 522 + 10 + filler.
    // Filler = 18 × 1.6 = 29, bumped to 30 so the window height is EVEN (an odd height centres on a half pixel).
    // Content = art 250 + 64 + 10 + panel 626 + 44 + 4 gaps × 13 = 1046 → window 1070.
    private static readonly float DcGroupH = DcCaptionH + DcS(4f) + DcRowsH;                                // 256
    private static readonly float DcRaidRest = DcPad + DcRArtH + DcHeaderH + DcBarH + DcPanelChrome
                                             + 2f * DcGroupH + DcPanelGap + DcPanelGap + DcPillH + 4f * DcGap;
    private static readonly float DcRaidFiller = DcS(18f) + (DcRaidRest + DcS(18f)) % 2f;                   // 30
    private static readonly float DcRH = DcRaidRest + DcRaidFiller;                                         // 1070
    // AFFIXES chip row (Plugin.DungeonConfirm.Model.cs DcFitAffixLabels): inner panel width, the key chip's estimated
    // width, and an icon chip's fixed parts (5 + icon 28 + 8 … 14).
    private static readonly float DcAffixRowW = Dc5W - DcPad - 2f * DcPanelPadX;                            // 624
    private static readonly float DcKeyChipW = DcS(84f), DcChipGap = DcSE(6f);                              // 134 · 10
    private static readonly float DcChipChrome = DcS(3f) + DcAffixIconW + DcS(5f) + DcS(9f);                // 55
    // Member rows are an EXACT fixed width, so every row of a list / group is the same and the scores and marks line up
    // across the raid columns (weighted cells split by content: the 2026-11-06 raid had 532 vs 422 px columns). A
    // bubble hugs its content's natural width up to MaxWidth, so the name cell is FIXED too (row − the fixed parts):
    // natural = row = MaxWidth. 5-player row = the PARTY panel's inner 624; raid column = (inner 968 − gap 12) / 2.
    private static readonly float Dc5RowW = DcAffixRowW;                                                     // 624
    private static readonly float DcRaidColW = 2f * MathF.Floor((DcRW - DcPad - 2f * DcPanelPadX - DcSp8) / 4f);   // 478
    private static readonly float DcRowLead = DcSp4 + DcClassW + DcSp7;                                     // 50
    private static readonly float Dc5NameW = Dc5RowW - DcRowLead - 2f * DcSp7 - DcScoreW - DcStatusW - DcSp6;   // 278
    private static readonly float DcRaidNameW = DcRaidColW - DcRowLead - 2f * DcSp7 - DcScoreRaidW - DcStatusH - DcSp4;   // 238

    private static readonly ColorRgba DcAmber   = Rgb(0xffcc73);
    private static readonly ColorRgba DcRedWash = Rgb(0xff4d4f, 0.22f);
    private static readonly ColorRgba DcPurple  = Rgb(0xc9a6ff), DcPurpleWash = Rgb(0xb385ff);

    private IWindowControl _dcWin5 = null!, _dcWinRaid = null!;
    private bool _dcShow5, _dcShowRaid;

    // ── theme-live tokens (SDL §3, current formulas: tiles and neutral pills carry the accent tint) ──────────────────
    private ColorRgba DcTile()    => Over(Menu.MenuAccent with { A = 0.06f }, Over(Menu.MenuText with { A = 0.08f }, PanelColor()));
    private ColorRgba DcNeutral() => Over(Menu.MenuAccent with { A = 0.08f }, Over(Menu.MenuMuted with { A = 0.22f }, PanelColor()));
    private ColorRgba DcWash(ColorRgba c, float a) => Over(c with { A = a }, PanelColor());
    private ColorRgba DcOn(ColorRgba fg, ColorRgba bg) => EnsureContrast(fg, bg, 3f);   // R7: solid fills keep white to 3:1
    private bool DcLow => _dc != null && _dcEnd == DcEnd.None && DcRemaining < 10;

    private void InitDcWindows()
    {
        _dcWin5    = DcRegister("dungeonhub.confirm.five", Dc5W, Dc5H, BuildDc5Root(),    () => _dcShow5);
        _dcWinRaid = DcRegister("dungeonhub.confirm.raid", DcRW, DcRH, BuildDcRaidRoot(), () => _dcShowRaid);
    }

    // Draggable while up, but every open lands dead centre (user, 2026-11-06). ShowTitleBar = false makes the WHOLE frame
    // the drag handle (the framework launcher tile's recipe). The pills still click: a press on one does arm a window drag
    // (WindowInteractionTicker only exempts scrollbars/slots/grips), but a still press moves nothing and a moving one
    // carries the pill along under the cursor, so the EventSystem click still lands. PersistIfSettled saves the drop and WindowSpec
    // has no "don't persist" switch, so the saved rect is overridden on each open instead (Plugin.DungeonConfirm.Centre.cs).
    // The declared height makes Anchor = Center's spawn fallback centre vertically too (ResolveAnchoredDefault centres on
    // DefaultRect.Height — 0 would pin the top at mid-screen).
    private IWindowControl DcRegister(string id, float width, float height, HudElement root, Func<bool> on)
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          id,
                Title:       "Dungeon Confirm",
                DefaultRect: new WindowRect(0f, 0f, width, height),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = false, StartVisible = false, ShowTitleBar = false, Anchor = WindowAnchor.Center,
              TitleProvider = () => L("dc.title"),
              FrameOpacity = DhFrameOpacity,   // settings' custom opacity, null = theme (.WindowOpacity.cs)
              ShouldRender = () => on() && _services.ClientState.Phase == GamePhase.World &&
                                   (_services.ClientState.UiState & GameUIState.Loading) == 0,
              // fw 2.30.0 pre-build (raid first open was 418 ms): filled off-screen with _dc == null — every lambda here is null-safe.
              Prebuild = true },
            Root: root,
            OnClose: DcClose));
        _windows.Add(w);
        return w;
    }

    // Show the layout for this request, in front, centred on the screen. BringToFront on every open: this window must
    // beat everything (it replaces a full-screen game prompt), and ZFront is sticky (WindowBuilder-Patterns.md) — that's
    // harmless here because the window is only ever up for a confirm. The hub (and its settings child) front on every open too.
    private void DcShow()
    {
        bool raid = _dc?.Twenty ?? false;
        var win = raid ? _dcWinRaid : _dcWin5;
        (raid ? _dcWin5 : _dcWinRaid).SetVisible(false);
        _dcShow5 = !raid; _dcShowRaid = raid;
        win.SetVisible(true);
        win.BringToFront();
        DcStartCentring();   // Plugin.DungeonConfirm.Centre.cs
    }

    private void DcHide()
    {
        _dcShow5 = _dcShowRaid = false;
        _dcWin5.SetVisible(false);
        _dcWinRaid.SetVisible(false);
        DcStopCentring();
        DcReleaseComp();   // no composed raid art kept while hidden (.ArtCompose.cs)
        DcSoundStopAll();  // hidden = nothing left to announce (.Sound.cs)
        _dc = null;
        _dcPreview = false;
    }

    // ── small text (TMP regular face at an explicit size; see the header note). size 0 = DcFsSmall ──────────────────
    private static TextElement DcSmall(Func<string> text, Func<ColorRgba> color, int size = 0, float width = 0f,
        TextAlign align = TextAlign.Left) =>
        new(text, Color: () => color(), Width: width, Align: align, NoWrap: true, FontSize: size > 0 ? size : DcFsSmall)
            { RichEmoji = true, Ellipsize = width > 0 };   // a fixed cell cuts a long translation with …

    private static TextElement DcBold(Func<string> text, Func<ColorRgba> color, int size, float width = 0f,
        TextAlign align = TextAlign.Left) =>
        new(text, Color: () => color(), Width: width, Align: align, NoWrap: true, FontSize: size) { Bold = true, Ellipsize = width > 0 };

    // Bold (TMP) labels sit ~1.5 px high per 28 px when line-box centred: put the label in a Column under a 2×drop
    // spacer, which centres the pair and moves the caps DOWN by DcBoldDrop (Maestro DropBold, R9 equal ink margins).
    // ⚠️ The Cell (no Width/Weight = flexibleWidth 0) is load-bearing: a Column's VLG force-expands its children, which
    // uGUI reports UP as flexibleWidth 1, so a bare Column in a Row HLG soaks up ALL the row's slack — Justify.Center
    // then has nothing to centre and a natural-width label sits left in a wide box ("✓ Ready" pushed left, 2026-11-06).
    private static HudElement DcDrop(HudElement label) => new CellElement(new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f * DcBoldDrop), label,
    }, Gap: 0.01f));

    // ── pills: round 44 px, fixed 186 px (the label box is 146 + 20 + 20), hover follows the shape ───────────────
    private sealed class DcHover { public bool On; }

    private static HudElement DcPillRow(HudElement label) => new RowElement(new HudElement[]
    {
        new SpacerElement(DcPillPadX, DcPillH), DcDrop(label), new SpacerElement(DcPillPadX, DcPillH),
    }, Gap: 0.01f, Justify: RowJustify.Center);

    private static TextElement DcPillLabel(Func<string> label, Func<ColorRgba> color) =>
        DcBold(label, color, DcFsBody, DcPillW - 2f * DcPillPadX, TextAlign.Center);

    // ⚠️ MaxWidth = DcPillW is load-bearing. MaxWidth 0 caps the bubble at its PARENT's width, and every footer pill's
    // parent is a ConditionalElement sized FROM the bubble — so the cap was the fresh GO's default RectTransform width
    // (100) and it never grew: a 100 px fill under a 116 px row that overflowed to the right ("Cancel Entry" ran off the
    // pill's right edge, 2026-11-06; BubbleBinding.Apply, WindowBuilder.Bindings.Misc.cs).

    // hoverFill: an explicit hover colour (the green Confirm); null = the usual 10 % text-colour wash over the fill.
    // Every pill (and its hover) follows the window opacity (Style.cs Fade), action pills included.
    private HudElement DcPill(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
                              Func<ColorRgba>? hoverFill = null)
    {
        var hover = new DcHover();
        return new BubbleElement(new SelectableElement(
            DcPillRow(DcPillLabel(label, text)), OnClick: onClick)
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(!hover.On ? fill() : hoverFill?.Invoke() ?? Over(Menu.MenuText with { A = 0.10f }, fill())),
            CornerRadius = DcPillH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DcPillW,
        };
    }

    // Same geometry, no click: fill halfway to the panel, muted label (Maestro's disabled pill).
    private HudElement DcPillOff(Func<string> label, Func<ColorRgba> fill) => new BubbleElement(
        DcPillRow(DcPillLabel(label, () => MutedColor())))
    {
        FillColorFn = () => Fade(Mix(PanelColor(), fill(), 0.5f)), CornerRadius = DcPillH / 2f - 1f, PaddingX = 0f, PaddingY = 0f,
        MaxWidth = DcPillW,
    };

    // Confirm (before pressing, team + match mode) is GREEN, not the theme accent (user, 2026-11-06): the same #22a35a
    // as the "Confirmed ✓" pill it turns into (Art.cs), white bold label as is — no EnsureContrast walk, same reason as
    // there — and a lighter green on hover instead of the text wash, which would grey the green out.
    private static readonly ColorRgba DcConfirmHoverFill = Rgb(0x2bb868);
    private HudElement DcPrimary(Func<string> l, Action a) =>
        DcPill(l, () => DcConfirmedFill, DcConfirmedText, a, () => DcConfirmHoverFill);
    private HudElement DcNeutralPill(Func<string> l, Action a) => DcPill(l, DcNeutral, () => EnsureContrast(TextColor(), DcNeutral(), 4.5f), a);
    private HudElement DcDanger(Func<string> l, Action a) => DcPill(l, () => DangerFill, () => DcOn(White, Over(DangerFill, PanelColor())), a);

    // ── panel: rounded Panel fill, caption + right-aligned hint on a 28 px strut, then children ──────────────────
    private HudElement DcPanel(Func<string> caption, HudElement hint, params HudElement[] children)
    {
        var items = new HudElement[children.Length + 1];
        items[0] = new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DcCaptionH),
            DcBold(caption, MutedColor, DcFsSmall),
            new SpacerElement(0f),   // flexible: pushes the hint to the right edge
            hint,
        }, Gap: 0.01f);
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: DcPanelGap))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = DcRadius, PaddingX = DcPanelPadX, PaddingY = DcPanelPadY,
        };
    }

    // "<b>N</b> / M ready" — N bold green.
    private HudElement DcReadyHint() => new RowElement(new HudElement[]
    {
        DcBold(() => _dc == null ? "" : _dc.ReadyCount.ToString(), () => StatusOn, DcFsSmall),
        DcWordSpace(),   // edge spaces in a run are dropped (header note)
        DcSmall(() => _dc == null ? "" : LF("dc.readyOf", _dc.Members.Count), MutedColor),
    }, Gap: 0.01f);

    // ── shared blocks ─────────────────────────────────────────────────────────────────────────────────────────────
    // Art strip: an empty rounded Tile while the texture loads (no layout jump), then the cropped art on top.
    private HudElement DcArtStrip(int width, int height) => new BubbleElement(
        new GameTextureElement(DcArtTex, width, height, () => _dcArtUv) { Fill = true, CornerRadius = (int)DcRadius })
    {
        FillColorFn = () => Fade(DcTile()), CornerRadius = DcRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private HudElement DcHeader() => new RowElement(new HudElement[]
    {
        DcIconTile(),   // Plugin.DungeonConfirm.Art.cs
        new CellElement(new ColumnElement(new HudElement[]
        {
            // Title + difficulty chip: the title ellipsizes (min width 0) and the fixed chip keeps its width, so the
            // chip sits right after the title and only the title gives way (same recipe as DcNameAndTags).
            new RowElement(new HudElement[]
            {
                new TextElement(() => DcTitle(), Color: () => TextColor(), NoWrap: true, FontSize: DcFsTitle)
                    { Bold = true, Ellipsize = true },
                new ConditionalElement(() => _dc is { DiffKind: > 0, TypeName.Length: > 0 }, DcDiffChip()),
            }, Gap: DcSp8),
            DcStatusLine(),
        }, Gap: DcS(2f)), Weight: 1f),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DcHeaderH),
            DcBold(DcTimerText, DcTimerColor, DcFsTimer, DcTimerW, TextAlign.Right),
        }, Gap: 0.01f),
    }, Gap: DcSp10);

    // 132×32 round chip, label centred (DcDrop). ⚠️ Explicit MaxWidth: a MaxWidth-0 bubble directly in a
    // ConditionalElement caps itself at the 100 px default (WindowBuilder-Patterns.md). Wash 22 % of the colour, text in
    // the colour (contrast-guarded); Master = red wash + light-red text, Nightmare = purple on a 25 % #b385ff wash.
    private HudElement DcDiffChip() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DcStatusH),
            DcDrop(new TextElement(() => DhWord(_dc?.TypeName), Color: () => EnsureContrast(DcDiffText(), DcDiffFill(), 4.5f),
                Width: DcDiffW - 2f * DcDiffPadX, Align: TextAlign.Center, NoWrap: true, FontSize: DcFsSmall)
                { Bold = true, Ellipsize = true }),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(DcDiffFill()), CornerRadius = DcStatusH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DcDiffW,
        }, Width: DcDiffW);

    private ColorRgba DcDiffText() => (_dc?.DiffKind ?? 0) switch
    {
        1 => StatusOn, 2 => DcAmber, 3 => ErrorText, 4 => DcPurple, _ => MutedColor(),
    };
    private ColorRgba DcDiffFill() => (_dc?.DiffKind ?? 0) switch
    {
        3 => Over(DcRedWash, PanelColor()), 4 => DcWash(DcPurpleWash, 0.25f), _ => DcWash(DcDiffText(), 0.22f),
    };

    // Three segments: regular · bold coloured · regular (the last one ellipsizes). Bold can't toggle live, so the
    // emphasis always sits in the middle segment and the texts move around it (DcStatus, Plugin.DungeonConfirm.Model.cs).
    // The segments are space-free at their edges; a word gap goes between two NON-empty neighbours only.
    private HudElement DcStatusLine() => new RowElement(new HudElement[]
    {
        DcSmall(() => DcStatus().a, MutedColor),
        new ConditionalElement(() => DcStatus() is { a.Length: > 0, b.Length: > 0 }, DcWordSpace()),
        DcBold(() => DcStatus().b, () => DcStatus().bKind switch { 1 => ErrorText, 2 => StatusOn, 3 => Menu.MenuAccent, _ => MutedColor() }, DcFsSmall),
        new ConditionalElement(() => DcStatus() is { c.Length: > 0 } s && (s.a.Length > 0 || s.b.Length > 0), DcWordSpace()),
        new CellElement(new TextElement(() => DcStatus().c, Color: () => MutedColor(), NoWrap: true, FontSize: DcFsSmall)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
    }, Gap: 0.01f);

    // One space at the small (19 px) size ≈ 5 px.
    private static HudElement DcWordSpace() => new SpacerElement(DcWordGap, 0f);

    private HudElement DcBar() => new BarElement(() => DcBarFraction(), Menu.MenuAccent)
    {
        // The fill fades like every other fill; the track is the framework's fixed black@0.38 tint (not themable).
        Height = DcBarH, FillWidth = true, FillColorFn = () => Fade(DcLow ? DcAmber : Menu.MenuAccent),
    };

    private float DcBarFraction() =>
        _dc == null || _dcEnd != DcEnd.None ? 0f : (float)Math.Clamp(DcRemaining / _dc.TotalSec, 0, 1);

    private string DcTimerText() => _dc == null ? "" : _dcEnd != DcEnd.None ? "–" : LF("dc.timer", (int)Math.Ceiling(DcRemaining));
    private ColorRgba DcTimerColor() => _dcEnd != DcEnd.None ? MutedColor() : DcLow ? DcAmber : Menu.MenuAccent;

    // Footer: muted message left, two fixed 186 px slots right. Every state shows a 186×44 element in each slot (an
    // invisible spacer when empty), so buttons never move.
    private HudElement DcFooter(Func<string> message) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(message, Color: () => MutedColor(), NoWrap: true, FontSize: DcFsSmall)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
        DcLeftSlot(),
        DcRightSlot(),
    }, Gap: DcSp8);

    private HudElement DcBlankSlot() => new SpacerElement(DcPillW, DcPillH);

    // Left: Decline (member / match) · "Decline?" armed (match) · disabled "Declined" (match, sent) · blank (leader, end).
    // Decline is Danger red like the leader's Cancel Entry (user, 2026-11-06); the sent "Declined" stays the grey off pill.
    private HudElement DcLeftSlot() => new ConditionalElement(() => _dc == null || _dcEnd != DcEnd.None || (!_dc.Match && _dc.IAmLeader),
        DcBlankSlot(),
        new ConditionalElement(() => _dcMatchDeclined,
            DcPillOff(() => L("dc.btn.declined"), DcNeutral),
            new ConditionalElement(() => DcNow <= _dcArmUntil,
                DcDanger(() => L("dc.btn.declineArm"), DcOnDecline),
                DcDanger(() => L("dc.btn.decline"), DcOnDecline))));

    // Right: Close (end) · Cancel Entry / disabled once "doing" (leader) · Confirm / green "Confirmed ✓" (Art.cs).
    private HudElement DcRightSlot() => new ConditionalElement(() => _dc == null || _dcEnd != DcEnd.None,
        DcNeutralPill(() => L("dc.btn.close"), DcClose),
        new ConditionalElement(() => !_dc!.Match && _dc.IAmLeader,
            new ConditionalElement(() => _dc!.Doing,
                DcPillOff(() => L("dc.btn.cancelEntry"), () => DangerFill),
                DcDanger(() => L("dc.btn.cancelEntry"), DcOnLeaderCancel)),
            new ConditionalElement(() => _dcMatchDeclined,
                DcPillOff(() => L("dc.btn.confirm"), () => DcConfirmedFill),
                new ConditionalElement(() => _dcConfirmed,
                    DcConfirmedPill(),
                    DcPrimary(() => L("dc.btn.confirm"), DcOnConfirm)))));

    // ── 5-player layout ───────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildDc5Root() => new ColumnElement(new HudElement[]
    {
        DcArtStrip((int)(Dc5W - DcPad), Dc5ArtH),
        DcHeader(),
        DcBar(),
        // AFFIXES only when there are any; without it the PARTY panel takes its height, so the window stays fixed.
        new ConditionalElement(() => _dc != null && _dc.Affixes.Count > 0,
            new ColumnElement(new HudElement[] { DcAffixPanel(), DcPartyPanel(Dc5Filler) }, Gap: DcGap),
            DcPartyPanel(Dc5FillerNoAffix)),
        DcFooter(() => _dcEnd != DcEnd.None ? DcClosingText() : ""),
    }, Gap: DcGap);

    private string DcClosingText() => LF("dc.closingIn", Math.Max(1, (int)Math.Ceiling(_dcEndUntil - DcNow)));

    private HudElement DcPartyPanel(float filler)
    {
        var rows = new HudElement[5];
        for (int i = 0; i < 5; i++) rows[i] = DcPartyRow(i);
        return DcPanel(() => L("dc.cap.party"), DcReadyHint(),
            new ColumnElement(rows, Gap: DcRowGap),
            new SpacerElement(0f, filler));
    }

    private DcMember? DcMem(int i) => _dc != null && i < _dc.Members.Count ? _dc.Members[i] : null;

    // One 42 px Tile row: class icon · name + ASSIST · ability score · status pill. My row: 1 px accent outline — that
    // alone marks me; the "YOU" tag came off both layouts (user, 2026-11-06), so the fixed name cell just gives names room.
    // Horizontal parts are all even (6+32+12 … 12+140+12+122+10 = 346), so the name cell is even too (624 − 346 = 278;
    // an 11-glyph name bold 21 + ASSIST ≈ 195). Exact 624 wide (Dc5RowW note); an unfilled slot = DcEmptyRow.
    private HudElement DcPartyRow(int i) => new ConditionalElement(() => DcMem(i) != null,
        new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(DcSp4, DcRowH),
            DcClassIcon(() => DcMem(i), i),
            new SpacerElement(DcSp7, 0f),
            new CellElement(DcNameAndTags(() => DcMem(i), withAssist: true), Width: Dc5NameW),
            new SpacerElement(DcSp7, 0f),
            DcSmall(() => DcScoreText(DcMem(i)), MutedColor, DcFsSmall, DcScoreW, TextAlign.Right),
            new SpacerElement(DcSp7, 0f),
            DcStatusPill(() => DcMem(i)),
            new SpacerElement(DcSp6, 0f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(DcTile()), CornerRadius = DcRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = Dc5RowW,
            OutlineColorFn = () => FadeRing(DcMem(i) is { Me: true } ? Menu.MenuAccent with { A = 0.6f } : null),
        },
        DcEmptyRow(Dc5RowW))
        { Prebuild = true, PrebuildName = "dc.member5" };   // see DcRaidRow

    // Unfilled slot (5-player and raid): the same 42 px pitch and width, a faint Tile (45 % of the way from the panel),
    // muted "Empty" where the name starts — no icon, score or mark (user, 2026-11-06).
    private HudElement DcEmptyRow(float rowW) => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(DcRowLead, DcRowH),
        new CellElement(DcSmall(() => L("dc.empty"), () => MutedColor() with { A = 0.6f }, DcFsBody), Width: rowW - DcRowLead),
    }, Gap: 0.01f))
    {
        FillColorFn = () => Fade(Mix(PanelColor(), DcTile(), 0.45f)), CornerRadius = DcRadius, PaddingX = 0f, PaddingY = 0f,
        MaxWidth = rowW,
    };

    // "AS (IBS)" = "66,084 (4,675)" (ability score, then Illusion-Breaking Strength); no IBS → "66,084"; a bot/AI or a
    // player whose score the game didn't send (match strangers) shows "—". ONE RichEmoji (TMP) text: the element colour
    // (muted) draws the score and an inline <color> tag draws the bracket one shade fainter — Muted 40 % toward the row
    // Tile, opaque (an <alpha> tag is TMP-only and would fight the element alpha). Cached per member (the label Func runs
    // every frame); the ink is theme-live, so the cache also keys on it and a theme change rebuilds the string.
    private string DcScoreText(DcMember? m)
    {
        if (m == null) return "";
        var ink = Mix(MutedColor(), DcTile(), 0.4f);
        if (m.ScoreText != null && m.ScoreInk == ink) return m.ScoreText;
        m.ScoreInk = ink;
        if (m.Ai || m.Score <= 0) return m.ScoreText = "—";
        var score = m.Score.ToString("N0", CultureInfo.InvariantCulture);
        if (m.Ibs <= 0) return m.ScoreText = score;
        return m.ScoreText = score + " <color=#" + DcHex(ink) + ">(" + m.Ibs.ToString("N0", CultureInfo.InvariantCulture) + ")</color>";
    }

    private static string DcHex(ColorRgba c) =>
        ((int)MathF.Round(c.R * 255f)).ToString("x2") + ((int)MathF.Round(c.G * 255f)).ToString("x2")
        + ((int)MathF.Round(c.B * 255f)).ToString("x2");

    // Bold name that ellipsizes (its min width is 0) next to the fixed-width ASSIST tag (min = preferred), so in the cell
    // only the name gives way and the tag stays right after it. No YOU tag: the accent outline marks my row.
    private HudElement DcNameAndTags(Func<DcMember?> m, bool withAssist) => new RowElement(new HudElement[]
    {
        new TextElement(() => m()?.Name ?? "", Color: () => TextColor(), NoWrap: true, FontSize: DcFsBody)
            { Bold = true, Ellipsize = true },
        new ConditionalElement(() => withAssist && m() is { Assist: true }, DcBold(() => L("dc.assist"), () => Menu.MenuAccent, DcFsTag, DcAssistW)),
    }, Gap: DcSp4);
}
