using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — the Master level grid (v4, replaces the v3 Pill dropdown): 20 level buttons in 2 rows of 10 under the
// difficulty chips, shown only while Master is the picked difficulty, + a one-line legend.
//
// Colours follow the game's own level list (master_challenge_dungeon_diff_item.lua: a check once cleared, a lock above
// max + 1): cleared (≤ GetMasterDungeonMaxDiff) = green wash + green text · next (max + 1) = plain neutral · locked
// (> max + 1) = dim and NOT clickable · selected = purple wash, purple ring, white text. A teal (accent) underline marks
// the levels whose data allows Solo AND Match (MasterChallengeDungeonTable.SingleAiMode == 1 and IsShowMatchBtn — only
// Master 1 and 6 in season 3), read per level from the M rows, never hard-coded.
//
// The selected ring is 2 mock px (3 px here); BubbleElement's own OutlineColorFn ring is a fixed 1 px, so the ring is
// an outer purple layer with the level's fill inset by DhLvRing. The underline lives inside that inner layer and is
// always built (accent or explicit Transparent) — never a Conditional inside a bubble's measured child.
public sealed partial class Plugin
{
    private const int DhLvCount = 20, DhLvPerRow = 10;
    private static readonly float DhLvRing = DcS(2f), DhLvRadius = DcS(7f), DhLvBarH = DcS(3f);            // 3 · 11 · 5
    private static readonly string[] DhLvNums = DhNumbers(DhLvCount);
    private static readonly string[] DhMasterLabels = DhNumbers(DhLvCount, "Master ");

    private static string[] DhNumbers(int n, string prefix = "")
    {
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = prefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    private DhLevel? DhLvAt(int diff) => diff >= 1 && diff <= _dhMaster.Count ? _dhMaster[diff - 1] : null;
    private bool DhLvSel(int diff) => DhIsMaster && _dhMasterDiff == diff;
    private bool DhLvClickable(int diff) => DhLvAt(diff) is { Unlocked: true };
    private bool DhLvSoloMatch(int diff) => DhLvAt(diff) is { HasSolo: true, Match: true };

    // The level's resting fill (no selection, no hover). An empty slot (rows not polled yet) reads as locked.
    private ColorRgba DhLvFill(int diff) => DhLvAt(diff) switch
    {
        { Unlocked: true, Cleared: true } => DcWash(DhGreen, 0.16f),
        { Unlocked: true }                => DcNeutral(),
        _                                 => DhLvLockedFill(),
    };

    private ColorRgba DhLvLockedFill() => Mix(PanelColor(), DcNeutral(), 0.4f);

    private ColorRgba DhLvText(int diff) => DhLvSel(diff) ? White : DhLvAt(diff) switch
    {
        { Unlocked: true, Cleared: true } => EnsureContrast(DhGreen, DhLvFill(diff), 3f),
        { Unlocked: true }                => TextColor(),
        _                                 => Mix(PanelColor(), TextColor(), 0.3f),
    };

    private HudElement DhMasterGrid()
    {
        var rows = new HudElement[DhLvCount / DhLvPerRow];
        for (int r = 0; r < rows.Length; r++)
        {
            var cells = new HudElement[DhLvPerRow];
            for (int c = 0; c < DhLvPerRow; c++) cells[c] = DhLvButton(r * DhLvPerRow + c + 1);
            rows[r] = new RowElement(cells, Gap: DhLvGapX);
        }
        return new ColumnElement(rows, Gap: DhLvGap);
    }

    private HudElement DhLvButton(int diff)
    {
        var hover = new DcHover();
        float innerW = DhLvW - 2f * DhLvRing, innerH = DhLvH - 2f * DhLvRing;
        float barW = innerW - 2f * DcS(4f);
        var inner = new CellElement(new BubbleElement(new ColumnElement(new HudElement[]
            {
                new SpacerElement(0f, DhLvBarH),   // mirrors the bar below, so the number stays centred
                new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, innerH - 2f * DhLvBarH),
                    DcDrop(DcBold(() => DhLvNums[diff - 1], () => DhLvText(diff), DhFsSmall, innerW, TextAlign.Center)),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                new RowElement(new HudElement[]
                {
                    new CellElement(new BubbleElement(new SpacerElement(barW, DhLvBarH))
                    {
                        FillColorFn = () => Fade(DhLvSoloMatch(diff) ? Menu.MenuAccent : Transparent),
                        CornerRadius = 2f, PaddingX = 0f, PaddingY = 0f, MaxWidth = barW,
                    }, Width: barW),
                }, Gap: 0.01f, Justify: RowJustify.Center),
            }, Gap: 0.01f))
            {
                FillColorFn = () => Fade(DhLvSel(diff) ? DcWash(DhPurple, 0.3f) : Transparent),
                CornerRadius = DhLvRadius - DhLvRing, PaddingX = 0f, PaddingY = 0f, MaxWidth = innerW,
            }, Width: innerW);
        return new CellElement(new BubbleElement(new SelectableElement(
                new RowElement(new HudElement[] { inner }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: () => DhSelectMaster(diff)) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(DhLvSel(diff) ? DhPurple
                    : hover.On && DhLvClickable(diff) ? Over(Menu.MenuText with { A = 0.08f }, DhLvFill(diff)) : DhLvFill(diff)),
                CornerRadius = DhLvRadius, PaddingX = DhLvRing, PaddingY = DhLvRing, MaxWidth = DhLvW,
            }, Width: DhLvW);
    }

    // cleared · next · locked · Solo + Match allowed — swatches in the button colours, muted small text.
    private HudElement DhMasterLegend()
    {
        float sw = DcSE(12f), gap = DcSE(4f), group = DcSE(10f);
        return new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhLegendH),
            DhSwatch(() => DcWash(DhGreen, 0.3f), sw, sw), new SpacerElement(gap, 0f), DcSmall(() => "cleared", MutedColor, DhFsSub),
            new SpacerElement(group, 0f),
            DhSwatch(() => DcNeutral(), sw, sw), new SpacerElement(gap, 0f), DcSmall(() => "next", MutedColor, DhFsSub),
            new SpacerElement(group, 0f),
            DhSwatch(() => Mix(PanelColor(), DcNeutral(), 0.5f), sw, sw), new SpacerElement(gap, 0f), DcSmall(() => "locked", MutedColor, DhFsSub),
            new SpacerElement(group, 0f),
            DhSwatch(() => Menu.MenuAccent, sw, DcSE(4f)), new SpacerElement(gap, 0f), DcSmall(() => "Solo + Match allowed", MutedColor, DhFsSub),
        }, Gap: 0.01f);
    }

    private HudElement DhSwatch(Func<ColorRgba> fill, float w, float h) => new CellElement(
        new BubbleElement(new SpacerElement(w, h))
        {
            FillColorFn = () => Fade(fill()), CornerRadius = MathF.Min(DcS(3f), h / 2f), PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
        }, Width: w);
}
