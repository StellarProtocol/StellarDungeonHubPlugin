using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — the 20-player raid layout (1024 × 1070 at DcScale 1.6): the same art / header / bar / footer as the
// 5-player window, no affixes, and a RAID panel with a 2 × 2 grid of groups (GROUP 1 | GROUP 2 over GROUP 3 | GROUP 4),
// five compact rows each: class icon · name (my row: accent outline, no tag) · a 32 px round status mark (✓ ready,
// … waiting, ✕ declined, ★ leader). Height breakdown: the constants block in Plugin.DungeonConfirm.Window.cs.
//
// Width: panel inner 1000 − 32 = 968; the two group columns split it around a 12 gap → 478 each (even, so nothing
// centres on a half pixel). Both columns are FIXED 478 cells and every row an exact 478 bubble (Dc5RowW note in
// .Window.cs): weighted cells sized to content gave 532 vs 422 px columns in the 2026-11-06 raid. A row = 6 + icon 32 +
// 12 + name + 12 + score 128 + 12 + mark 32 + 6 → the name cell is a fixed 238 (a 16-glyph bold-21 name ≈ 180, so
// the small "AS (IBS)" score still costs no name). Every group always shows 5 rows; an unfilled slot is DcEmptyRow.
public sealed partial class Plugin
{
    private HudElement BuildDcRaidRoot() => new ColumnElement(new HudElement[]
    {
        DcArtStrip((int)(DcRW - DcPad), DcRArtH),
        DcHeader(),
        DcBar(),
        // Caption by the CONTENT, layout by the team: a 20-person party in a 5-player dungeon is a PARTY (Lua.cs "raidContent").
        DcPanel(() => _dc?.RaidContent == false ? "PARTY" : "RAID", DcReadyHint(),
            new RowElement(new HudElement[]
            {
                new CellElement(new ColumnElement(new HudElement[] { DcGroup(0), DcGroup(2) }, Gap: DcPanelGap), Width: DcRaidColW),
                new CellElement(new ColumnElement(new HudElement[] { DcGroup(1), DcGroup(3) }, Gap: DcPanelGap), Width: DcRaidColW),
            }, Gap: DcSp8),
            new SpacerElement(0f, DcRaidFiller)),
        DcFooter(DcRaidMessage),
    }, Gap: DcGap);

    private string DcRaidMessage()
    {
        if (_dc == null) return "";
        if (_dcEnd != DcEnd.None) return DcClosingText();
        int w = _dc.Waiting;
        return w == 0 ? "" : $"{w} still waiting";
    }

    private DcMember? DcCell(int cell) =>
        _dc != null && cell >= 0 && cell < 20 && _dcGrid[cell] is var i && i >= 0 && i < _dc.Members.Count ? _dc.Members[i] : null;

    private HudElement DcGroup(int g)
    {
        var rows = new HudElement[5];
        for (int p = 0; p < 5; p++) rows[p] = DcRaidRow(g * 5 + p);
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DcCaptionH),
                DcBold(() => $"GROUP {g + 1}", MutedColor, DcFsSmall),
                new SpacerElement(0f),
                DcSmall(() => DcGroupHint(g), MutedColor),
            }, Gap: 0.01f),
            new ColumnElement(rows, Gap: DcRowGap),
        }, Gap: DcS(4f));
    }

    // "ready/5" of the group ("1/5"), against the 5 SLOTS, so an empty group reads "0/5" (user, 2026-11-06). The
    // overall "N / M ready" hint stays against the actual member count.
    private string DcGroupHint(int g)
    {
        int ready = 0;
        for (int p = 0; p < 5; p++) if (DcCell(g * 5 + p) is { Ready: 1 }) ready++;
        return $"{ready}/5";
    }

    private HudElement DcRaidRow(int cell) => new ConditionalElement(() => DcCell(cell) != null,
        new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(DcSp4, DcRowH),
            DcClassIcon(() => DcCell(cell), 5 + cell),   // icon uv slots 5..24 (0..4 = the 5-player rows)
            new SpacerElement(DcSp7, 0f),
            new CellElement(DcNameAndTags(() => DcCell(cell), withAssist: false), Width: DcRaidNameW),
            new SpacerElement(DcSp7, 0f),
            DcSmall(() => DcScoreText(DcCell(cell)), MutedColor, DcFsTag, DcScoreRaidW, TextAlign.Right),
            new SpacerElement(DcSp7, 0f),
            DcMark(() => DcCell(cell)),
            new SpacerElement(DcSp4, 0f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(DcTile()), CornerRadius = DcRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DcRaidColW,
            OutlineColorFn = () => FadeRing(DcCell(cell) is { Me: true } ? Menu.MenuAccent with { A = 0.6f } : null),
        },
        DcEmptyRow(DcRaidColW));

    // 32 px round mark (R1 radius 15) on the state's wash. The leader's ★ (20 px) and the ✓ / ✕ (16 px) are generated
    // PNGs (Art.cs): ★ is not verified in the game fonts, and bold ✕ drew blank in the header. Only "…" stays text
    // (both shipped TMP faces carry U+2026).
    private HudElement DcMark(Func<DcMember?> m)
    {
        static HudElement Centred(Func<byte[]?> png, float w) => new RowElement(new HudElement[]
        {
            new SpacerElement((DcStatusH - w) / 2f, DcStatusH), new ImageElement(png, (int)w, (int)w),
            new SpacerElement((DcStatusH - w) / 2f, DcStatusH),
        }, Gap: 0.01f, Justify: RowJustify.Center);
        return new CellElement(new BubbleElement(
            new ConditionalElement(() => DcState(m()) == 4, Centred(DcStarPng, DcStarW),
            new ConditionalElement(() => DcState(m()) == 1, Centred(() => DcGlyphPng(DcGlyph.Check, DcGlyphLW, DcPillTextS(1)), DcGlyphLW),
            new ConditionalElement(() => DcState(m()) is 3 or 5, Centred(() => DcGlyphPng(DcGlyph.Cross, DcGlyphLW, DcPillTextS(3)), DcGlyphLW),
                new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, DcStatusH),
                    DcDrop(DcBold(() => DcState(m()) == 2 ? "…" : "", () => DcPillText(m()), DcFsMark, DcStatusH, TextAlign.Center)),
                }, Gap: 0.01f, Justify: RowJustify.Center)))))
            {
                FillColorFn = () => Fade(DcPillFill(m())), CornerRadius = DcStatusH / 2f - 1f, PaddingX = 0f, PaddingY = 0f,
                MaxWidth = DcStatusH,
            }, Width: DcStatusH);
    }
}
