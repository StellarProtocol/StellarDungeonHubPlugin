using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — the right-hand panel: art band, name, DIFFICULTY (chips + the Master level grid), the info tiles,
// and HOW TO GO (Solo / Match / Party). Activities (World Boss / Hunt / Other tabs) get a minimal variant: art,
// name, tiles, and Open — World Boss instead: its event-timer status and one Match (.WorldBoss.cs).
//
// Nothing moves between states: each button is a fixed 210/212 × 64 slot that is either a clickable pill or its
// static twin (same geometry, same Func label / sub-line), swapped by a ConditionalElement around the WHOLE bubble
// (never a Conditional inside a bubble's measured child — WindowBuilder-Patterns.md §BubbleElement).
public sealed partial class Plugin
{
    private readonly record struct DhBtn(bool Enabled, bool Active, string Label, string Sub);

    // Solo = 0, Match = 1, Party = 2. Order of the reasons: the live match first (it owns the buttons while it runs),
    // then the level's lock, what the level allows, and finally the party role (only the leader can start anything).
    private DhBtn DhBtnState(int which)
    {
        string label = L(which == 0 ? "dh.mode.solo" : which == 1 ? "dh.mode.match" : "dh.mode.party");
        var e = DhCur; var live = _dhLive;
        bool member = live.InTeam && !live.Leader;
        if (e == null) return new(false, false, label, "");
        if (DhIsGh(e)) return DhGhBtnState(which, label);   // Guild Hunt: enters like a dungeon (.GuildHunt.cs)
        if (e.IsActivity)
        {
            if (!e.CanMatch) return new(false, false, label, L("dh.sub.notOffered"));
            if (live.Matching && DhMatchingThis()) return new(false, true, L("dh.mode.matching"), DhClock());
            if (e.Locked) return new(false, false, label, L("dh.sub.locked"));
            // World Boss: only inside its event window (timer 220), never "Open" just because the function is on
            if (DhIsWb(e) && _dhWb is { Open: false }) return new(false, false, label, DhWbClosedSub());
            if (live.Matching) return new(!member, false, label, member ? L("dh.sub.leaderOnly") : L("dh.sub.changeMatch"));
            return member ? new(false, false, label, L("dh.sub.leaderOnly")) : new(true, false, label, L("dh.sub.matchmaking"));
        }
        var lv = DhCurLevel;
        if (lv == null) return new(false, false, label, L("dh.sub.notOnLevel"));
        if (live.Matching)
        {
            if (which != 1) return new(false, false, label, "");
            if (DhMatchingThis()) return new(false, true, L("dh.mode.matching"), DhClock());
            return member ? new(false, false, label, L("dh.sub.leaderOnly")) : new(lv.Match && lv.Unlocked, false, label, lv.Match ? L("dh.sub.changeMatch") : L("dh.sub.notOnLevel"));
        }
        if (!lv.Unlocked) return new(false, false, label, L("dh.sub.locked"));
        switch (which)
        {
            case 0:
                if (!lv.HasSolo) return new(false, false, label, L("dh.sub.notOnLevel"));
                if (!lv.SoloOk) return new(false, false, label, lv.SoloWhy.Length > 0 ? lv.SoloWhy : L("dh.sub.notUnlocked"));
                return member ? new(false, false, label, L("dh.sub.leaderOnly")) : new(true, false, label, L("dh.sub.withBots"));
            case 1:
                if (!lv.Match) return new(false, false, label, L("dh.sub.notOnLevel"));
                return member ? new(false, false, label, L("dh.sub.leaderOnly")) : new(true, false, label, L("dh.sub.matchmaking"));
            default:
                return member ? new(false, false, label, L("dh.sub.leaderOnly")) : new(true, false, label, L("dh.sub.withParty"));
        }
    }

    // ── dungeon / raid panel ──────────────────────────────────────────────────────────────────────────────────────
    // Under the chips sits ONE fixed-height block (DhMidBlock), so the buttons stay anchored at the bottom.
    // Hero chips: an Unstable entry (v5) shows one selected light-blue "Unstable" chip; a regular dungeon Normal · Hard
    // (· Master). Raid: Easy · Hard · Nightmare where the group has them.
    private HudElement DhDetailPanel() => DhPanelNoCaption(
        DhArtBand(),
        DhNameRow(),
        DhCaption(() => L("dh.cap.difficulty")),
        new ConditionalElement(() => _dhTab == DhTabRaid,
            new RowElement(new HudElement[]
            {
                DhChipIf(0, DhChipW), DhChipIf(1, DhChipW), DhChipIf(2, DhChipWideW),
            }, Gap: DhPanGap),
            new ConditionalElement(() => DhCur is { Unstable: true },
                new RowElement(new HudElement[] { DhDiffChip(DhUnstableIdx, DhChipWideW) }, Gap: DhPanGap),
                new RowElement(new HudElement[]
                {
                    DhDiffChip(1, DhChipW), DhDiffChip(2, DhChipW),
                    new ConditionalElement(() => DhCur is { MasterCount: > 0 }, DhDiffChip(DhMasterIdx, DhChipWideW)),
                }, Gap: DhPanGap))),
        DhMidBlock(),
        DhCaption(() => L("dh.cap.howToGo")),   // outside the block: always directly above the buttons
        new RowElement(new HudElement[]
        {
            DhGoButton(0, DhThirdW, DhOnSolo),
            DhGoButton(1, DhThirdMidW, DhOnMatch),
            DhGoButton(2, DhThirdW, DhOnParty),
        }, Gap: DhPanGap));

    // The block between the chips and the HOW TO GO caption: a DhMidH strut beside a TOP-aligned Fill Conditional
    // (Master = grid · legend · tiles, else tiles). Its height is max(strut, content), and DhMidH carries 26 px
    // (DhMidSlack) of headroom over the Master content, so it is the strut in every state and the window never changes
    // height. v4 instead relied on two variants summing to the same number on paper; text taller than its strut (legend
    // 18 px in 22, the grid's bold label in 28) made Master ~14 px taller in game, the window grew, and the vertically
    // centred list moved down by half of that.
    private HudElement DhMidBlock() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, DhMidH),
        new CellElement(new ConditionalElement(() => DhIsMaster,
            new ColumnElement(new HudElement[]
            {
                DhMasterGrid(), DhMasterLegend(), DhInfoTiles(),
            }, Gap: DhPanGap),
            new ColumnElement(new HudElement[] { DhInfoTiles() }, Gap: DhPanGap), Fill: true), Width: DhDetInW),
    }, Gap: 0.01f);

    // Recommended · Your score · third tile: Normal / Hard "First clear" (Reward ready green / Claimed muted, v5), Master
    // "First clear", Unstable / raid "Rewards left" ("0 / N · no rewards" muted: info only). Unstable's Recommended is the
    // game's own text (RecommendFightValue is a meaningless 100 there).
    private HudElement DhInfoTiles() => new RowElement(new HudElement[]
    {
        DhTile(() => L("dh.tile.recommended"), () => DhCurLevel switch
            {
                { RecText.Length: > 0 } l => l.RecText,
                { Rec: > 0 } l => DhNum(l.Rec),
                _ => "—",
            }, () => TextColor(), DhThirdW),
        DhTile(() => L("dh.tile.yourScore"), () => _dhLive.MyScore > 0 ? DhNum(_dhLive.MyScore) : "—", DhScoreColor, DhThirdMidW),
        DhTile(() => DhCurLevel?.CounterKey is { Length: > 0 } k ? k : L("dh.word.rewardsLeft"),
               () => DhCurLevel?.TileVal is { Length: > 0 } v ? v : "—",
               () => DhCurLevel switch
               {
                   { FirstClear: 1 } => DhGreen,
                   { FirstClear: 2 } or { CounterZero: true } => MutedColor(),
                   _ => TextColor(),
               }, DhThirdW),
    }, Gap: DhPanGap);

    private HudElement DhPanelNoCaption(params HudElement[] children) => new BubbleElement(new ColumnElement(children, Gap: DhPanGap))
    {
        FillColorFn = () => Fade(PanelColor()), CornerRadius = DhRadius, PaddingX = DhPanPadX, PaddingY = DhPanPadY,
    };

    private HudElement DhNameRow() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, DhNameH),
        new CellElement(new TextElement(() => (DhCur is { Title.Length: > 0 } t ? t.Title : DhCur?.Name) ?? (L(_dhHaveCatalog ? "dh.name.empty" : "dh.name.loading")),
            Color: () => TextColor(), NoWrap: true, FontSize: DhFsTitle) { Bold = true, Ellipsize = true }, Weight: 1f),
    }, Gap: 0.01f);

    private static string DhNum(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

    // Your score: green when it reaches the level's recommendation, amber below it (muted with nothing to compare).
    private ColorRgba DhScoreColor()
    {
        var l = DhCurLevel;
        if (l == null || l.Rec <= 0 || _dhLive.MyScore <= 0) return TextColor();
        return _dhLive.MyScore >= l.Rec ? DhGreen : DhAmber;
    }

    // ── difficulty chips: 44 px round, bold label; selected = 25 % wash of its colour + a 60 % ring ──────────────────
    // Hero: Normal · Hard · Master ("Master N" while picked; purple text at rest, v4 mock .dc.pi); an Unstable entry has
    // only its own light-blue "Unstable" chip (v5 — the v4 dimmed Unstable chip on regular dungeons is gone).
    // Raid: Easy · Hard · Nightmare, each only when the group has it (a Backtrack group has one chip, labelled "Normal").
    private static readonly string[] DhHeroDiffs = { "dh.diff.unstable", "dh.diff.normal", "dh.diff.hard" },
                                     DhRaidDiffs = { "dh.diff.easy", "dh.diff.hard", "dh.diff.nightmare" };   // catalog keys

    private ColorRgba DhKindColor(int idx) => _dhTab == DhTabRaid
        ? idx switch { 0 => DhGreen, 1 => DhAmber, _ => DhPurple }
        : idx switch { 0 => DhBlue, 1 => DhGreen, 2 => DhAmber, _ => DhPurple };

    private bool DhChipHas(int idx) => DhCur is { } e && (idx == DhMasterIdx ? e.Tab == DhTabHero && e.MasterCount > 0 : e.Levels[idx] != null);
    private bool DhChipOn(int idx) => _dhLevelIdx == idx && DhChipHas(idx);
    private bool DhChipOpen(int idx) => DhCur is { } e && (idx == DhMasterIdx ? DhChipHas(idx) && !e.Locked : e.Levels[idx] is { Unlocked: true });

    private string DhChipLabel(int idx)
    {
        if (_dhTab == DhTabRaid) return DhCur?.Levels[idx]?.Label is { Length: > 0 } l ? l : L(DhRaidDiffs[Math.Min(idx, 2)]);
        if (idx != DhMasterIdx) return L(DhHeroDiffs[idx]);
        return DhIsMaster && _dhMasterDiff >= 1 && _dhMasterDiff <= DhLvCount ? DhMasterLabel(_dhMasterDiff) : L("dh.diff.master");
    }

    // Raid chips exist only where the group has that difficulty.
    private HudElement DhChipIf(int idx, float w) => new ConditionalElement(() => DhCur?.Levels[idx] != null, DhDiffChip(idx, w));

    private HudElement DhDiffChip(int idx, float w)
    {
        var hover = new DcHover();
        Func<ColorRgba> fill = () => DhChipOn(idx) ? DcWash(DhKindColor(idx), 0.25f)
                                    : hover.On ? Over(Menu.MenuText with { A = 0.08f }, DcNeutral()) : DcNeutral();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, DhChipH),
                DcDrop(DcBold(() => DhChipLabel(idx),
                    () => DhChipOn(idx) ? EnsureContrast(DhKindColor(idx), fill(), 4.5f)
                        : idx == DhMasterIdx && _dhTab == DhTabHero && DhChipOpen(idx) ? EnsureContrast(DhPurple, fill(), 4.5f)
                        : DhChipOpen(idx) ? MutedColor() : MutedColor() with { A = 0.5f },
                    DhFsBody, w, TextAlign.Center)),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: () => DhSelectLevel(idx)) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(fill()),
                OutlineColorFn = () => FadeRing(DhChipOn(idx) ? DhKindColor(idx) with { A = 0.6f } : null),
                CornerRadius = DhChipH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            }, Width: w);
    }

    // ── info tile: muted label over a bold value, 66 px, fixed width ─────────────────────────────────────────────
    private HudElement DhTile(Func<string> label, Func<string> value, Func<ColorRgba> valueColor, float w) => new CellElement(
        new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(DcSE(8f), DhTileH),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(label, Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub) { RichEmoji = true, Ellipsize = true },
                new TextElement(value, Color: () => valueColor(), NoWrap: true, FontSize: DhFsBody) { Bold = true, Ellipsize = true },
            }, Gap: DcS(1f)), Width: w - 2f * DcSE(8f)),
            new SpacerElement(DcSE(8f), 0f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(DcTile()), CornerRadius = DhRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
        }, Width: w);

    // ── the three "how to go" buttons ────────────────────────────────────────────────────────────────────────────
    private HudElement DhGoButton(int which, float w, Action onClick)
    {
        var hover = new DcHover();
        // v5: each mode its own fill — Solo blue / dark navy text, Match the accent, Party coral / dark brown text (fixed
        // colours, not theme-live: they name the mode). Unavailable = the same grey for all three (offFill below).
        Func<ColorRgba> onFill = () => which switch { 0 => DhSoloFill, 1 => Menu.MenuAccent, _ => DhPartyFill };
        Func<ColorRgba> onText = () => which switch { 0 => DhSoloInk, 1 => DcOn(White, Menu.MenuAccent), _ => DhPartyInk };
        Func<ColorRgba> offFill = () => DhBtnState(which).Active ? DcWash(Menu.MenuAccent, 0.35f) : Mix(PanelColor(), DcNeutral(), 0.55f);
        Func<ColorRgba> offText = () => DhBtnState(which).Active
            ? EnsureContrast(Mix(Menu.MenuAccent, new ColorRgba(0f, 0f, 0f), 0.75f), offFill(), 4.5f)
            : Mix(PanelColor(), TextColor(), 0.4f);
        return new CellElement(new ConditionalElement(() => DhBtnState(which).Enabled,
            new BubbleElement(new SelectableElement(DhGoContent(which, onText, w), OnClick: onClick)
                { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(hover.On ? Over(Menu.MenuText with { A = 0.10f }, onFill()) : onFill()),
                CornerRadius = DhBtnRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            },
            new BubbleElement(DhGoContent(which, offText, w))
            {
                FillColorFn = () => Fade(offFill()),
                CornerRadius = DhBtnRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            }), Width: w);
    }

    // Bold label over the small reason line, both centred in the fixed width (the line ellipsizes a long condition).
    private HudElement DhGoContent(int which, Func<ColorRgba> text, float w) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, DhBtnH),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => DhBtnState(which).Label, Color: () => text(), Width: w, Align: TextAlign.Center, NoWrap: true,
                FontSize: DhFsBody) { Bold = true, Ellipsize = true },
            new TextElement(() => DhBtnState(which).Sub, Color: () => text() with { A = 0.8f }, Width: w - DcSE(12f),
                Align: TextAlign.Center, NoWrap: true, FontSize: DhFsTiny) { RichEmoji = true, Ellipsize = true },
        }, Gap: 0.01f)),
    }, Gap: 0.01f, Justify: RowJustify.Center);

    // ── activity panel (World Boss / Hunt / Other) ───────────────────────────────────────────────────────────────
    // World Boss: "This week: <boss>" line, three tiles (Rewards left · Status from its event timer · Opens) and ONE
    // full-width Match (no "Open the game's page"). Hunt / Other: the two tiles + Open (+ an empty Match slot) as before;
    // their Status is still the function switch only. The sub-line strut is there for every tab, so heights match.
    private HudElement DhActivityPanel() => DhPanelNoCaption(
        DhArtBand(),
        DhNameRow(),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DhActSubH),
            new CellElement(new TextElement(DhActSubLine, Color: () => MutedColor(), NoWrap: true, FontSize: DhFsSub)
                { RichEmoji = true, Ellipsize = true }, Weight: 1f),
        }, Gap: 0.01f),
        new ConditionalElement(() => DhIsWb(DhCur), DhWbTiles(), new RowElement(new HudElement[]
        {
            DhTile(() => DhCur?.CounterKey is { Length: > 0 } k ? k : L("dh.word.rewardsLeft"),
                   () => DhCur?.TileVal is { Length: > 0 } v ? v : "—",
                   () => DhCur is { CounterZero: true } ? MutedColor() : TextColor(), DhHalfW),
            DhTile(() => L("dh.tile.status"), () => L(DhCur is { Locked: true } ? "dh.word.locked" : "dh.st.open"),
                   () => DhCur is { Locked: true } ? MutedColor() : DhGreen, DhHalfW),
        }, Gap: DhPanGap)),
        new SpacerElement(0f, DhActFiller),
        DhCaption(() => L("dh.cap.go")),
        new ConditionalElement(() => DhIsWb(DhCur), DhGoButton(1, DhDetInW, DhOnMatch), new RowElement(new HudElement[]
        {
            new CellElement(new ConditionalElement(() => DhCur is { Locked: false },
                new BubbleElement(new SelectableElement(DhStaticContent(() => L("dh.act.open"), () => L("dh.act.page"),
                        () => EnsureContrast(TextColor(), DcNeutral(), 4.5f), DhHalfW), OnClick: DhOnActivityOpen) { Bare = true })
                    { FillColorFn = () => Fade(DcNeutral()), CornerRadius = DhBtnRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhHalfW },
                new BubbleElement(DhStaticContent(() => L("dh.act.open"), () => L("dh.sub.locked"), () => Mix(PanelColor(), TextColor(), 0.4f), DhHalfW))
                    { FillColorFn = () => Fade(Mix(PanelColor(), DcNeutral(), 0.55f)), CornerRadius = DhBtnRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhHalfW }),
                Width: DhHalfW),
            // Match only where the game has an activity match (World Boss); otherwise the slot stays empty.
            new CellElement(new ConditionalElement(() => DhCur is { CanMatch: true }, DhGoButton(1, DhHalfW, DhOnMatch),
                new SpacerElement(DhHalfW, DhBtnH)), Width: DhHalfW),
        }, Gap: DhPanGap)));

    private HudElement DhStaticContent(Func<string> label, Func<string> sub, Func<ColorRgba> text, float w) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, DhBtnH),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(label, Color: () => text(), Width: w, Align: TextAlign.Center, NoWrap: true, FontSize: DhFsBody) { Bold = true, Ellipsize = true },
            new TextElement(sub, Color: () => text() with { A = 0.8f }, Width: w - DcSE(12f), Align: TextAlign.Center, NoWrap: true,
                FontSize: DhFsTiny) { RichEmoji = true, Ellipsize = true },
        }, Gap: 0.01f)),
    }, Gap: 0.01f, Justify: RowJustify.Center);
}
