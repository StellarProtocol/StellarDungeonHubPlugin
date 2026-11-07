using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — member status: the 5-player status pill and the state → colour tokens it shares with the raid
// round marks (Plugin.DungeonConfirm.Raid.cs DcMark). Split from .Window.cs (500-line cap).
public sealed partial class Plugin
{
    // 122×32 status pill (R1 radius 15): Leader amber · Ready green · Waiting… muted · Declined / No answer red — text
    // only, centred (user, 2026-11-06: the ✓ / ✕ images came off the 5-player pills; the raid round marks keep theirs).
    private HudElement DcStatusPill(Func<DcMember?> m) => new CellElement(new BubbleElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, DcStatusH),
            DcDrop(DcBold(() => DcStatusLabel(m()), () => DcPillText(m()), DcFsSmall, DcStatusW, TextAlign.Center)),
        }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(DcPillFill(m())), CornerRadius = DcStatusH / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DcStatusW,
        }, Width: DcStatusW);

    // Leader shows "Leader" only in team mode (match has no leader); a leader who declined can't happen (they cancel).
    // State 5 "No answer" (Ready 3, set only here in C# by DcMarkTimeouts) = Declined's red look: red wash + ink, the ✕ mark.
    private static int DcState(DcMember? m) => m == null ? 0 : m.Ready == 2 ? 3 : m.Ready == 3 ? 5 : m.Leader ? 4 : m.Ready == 1 ? 1 : 2;
    private static string DcStatusLabel(DcMember? m) => DcState(m) switch
    {
        1 => "Ready", 2 => "Waiting…", 3 => "Declined", 4 => "Leader", 5 => "No answer", _ => "",
    };

    // "Time ran out", team mode: mark who didn't answer (Ready 3) and name them in the header (_dcEndName / _dcEndCount,
    // DcStatus). Source = this request's TimeOut votes (Lua head 'timeouts': the server sends one per silent member,
    // code 4; the leader sends none). None recorded / none in our list (our countdown can reach 0 before they land) →
    // the members still waiting, minus the leader. Re-run from the tick when late votes arrive, so the previous pass is
    // undone first (_dcSilent keeps each row's prior Ready). A Declined row is never overwritten — a decline wins.
    private readonly List<(DcMember m, int was)> _dcSilent = new();
    private string _dcToutsApplied = "";

    private void DcMarkTimeouts(string touts)
    {
        _dcToutsApplied = touts;
        foreach (var (m, was) in _dcSilent) m.Ready = was;
        _dcSilent.Clear();
        _dcEndName = ""; _dcEndCount = 0;
        if (_dc == null || _dc.Match) return;   // match mode keeps the plain "Time ran out"
        if (touts.Length > 0)
        {
            var ids = new HashSet<string>(touts.Split(','));
            foreach (var m in _dc.Members) if (m.Ready != 2 && ids.Contains(m.CharId)) _dcSilent.Add((m, m.Ready));
        }
        if (_dcSilent.Count == 0)
            foreach (var m in _dc.Members) if (m.Ready == 0 && !m.Leader) _dcSilent.Add((m, m.Ready));
        foreach (var (m, _) in _dcSilent) m.Ready = 3;
        _dcEndCount = _dcSilent.Count;
        if (_dcEndCount == 1) _dcEndName = _dcSilent[0].m.Me ? "You" : _dcSilent[0].m.Name;
    }
    // State colours (raid marks + 5-player pills) = "style A", the difficulty chip's look: a 22 % wash of the colour over
    // the Tile and a BRIGHT tint of the same hue as the bold label / ✓ / ✕ / ★, used AS IS. The 45 % wash + EnsureContrast
    // (…, 4.5) tried in 58f44fe walked the inks to grey / brown and gave Ready dark text (user, 2026-11-06) — do not
    // contrast-adjust these. Waiting stays the muted 18 % wash with its 3:1 contrast hold.
    private const float DcStateWashA = 0.22f;
    private static readonly ColorRgba DcRedBase   = Rgb(0xff4d4f);
    private static readonly ColorRgba DcGreenInk  = Rgb(0x6ee69a), DcAmberInk = Rgb(0xffd27f), DcRedInk = Rgb(0xff9c9d);

    private ColorRgba DcStateWash(int s) => s switch
    {
        1 => StatusOn, 3 or 5 => DcRedBase, 4 => DcAmber, _ => MutedColor(),
    };
    private ColorRgba DcStateInk(int s) => s switch
    {
        1 => DcGreenInk, 3 or 5 => DcRedInk, 4 => DcAmberInk, _ => MutedColor(),
    };
    private ColorRgba DcPillFill(DcMember? m) => DcPillFillS(DcState(m));
    private ColorRgba DcPillFillS(int s) => s == 2 || s == 0
        ? Over(MutedColor() with { A = 0.18f }, DcTile())
        : Over(DcStateWash(s) with { A = DcStateWashA }, DcTile());
    private ColorRgba DcPillText(DcMember? m) => DcPillTextS(DcState(m));
    private ColorRgba DcPillTextS(int s) => s == 2 || s == 0 ? EnsureContrast(MutedColor(), DcPillFillS(s), 3f) : DcStateInk(s);
}
