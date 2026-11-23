using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeons hub — the list is a VirtualListElement (2026-11-23): a small recycled pool of row slots over the whole list,
// so any number of entries shows (it was a fixed 20-slot ListElement, and the NPC tab's 20 NPCs had filled every slot).
//   • Mapping: OnWindow(first) stores _dhListFirst BEFORE the slot values are pulled, and pool slot k shows DATA row
//     first + k. Every per-slot Func (.Window.cs DhListSlot / DhListRow, .Art.cs DhThumb, .NpcPins.cs DhNpcRowStar) and
//     the row click resolve that data index on each read — never the slot index.
//   • VariableHeight: a row is 84 + the 10 px gap (pitch 94), the LAST row has no gap, and the first row of a group
//     carries its 22 px header ("OPEN TODAY" / "DUNGEONS", "PINNED" / "ALL") — so rows are measured, not uniform.
//     RowHeight is only the estimate seed (the plain 94 pitch). Header / gap are Conditionals in the plain slot Column,
//     outside every bubble (WindowBuilder-Patterns.md: never a Conditional inside a bubble's measured child).
//   • RowRevision: a latched row height is keyed by data index. What can change it without a reset — the Unstable set
//     arriving on a catalog parse (headers move), the list growing (the old last row gains its gap) — is folded into a
//     per-row revision, so the list re-measures just that row in place (no blank).
//   • ResetScroll: back to the top on a tab switch, a search change and a pin toggle (regroup). It also forgets the
//     measured heights, so the rows re-measure (~2 frames, framework parks them off-screen meanwhile). A catalog parse
//     (every 3 s while open) does NOT reset: the reader keeps their place.
//   • Pool: the 706 px viewport holds ≤ 9 rows of the shortest (84 px) height; 11 slots = that + 2 spare.
public sealed partial class Plugin
{
    private const int DhListPool = 11;
    private static readonly float DhListPitch = DcSE(52f) + DcSE(6f);   // DhRowH 84 + DhRowPitchPad 10 = 94 (estimate seed)

    private int _dhListFirst;    // data index pool slot 0 shows (OnWindow)
    private bool _dhListReset;   // one-shot ResetScroll pulse (tab / search / pin)

    private HudElement DhVirtualList()
    {
        var pool = new HudElement[DhListPool];
        for (int k = 0; k < DhListPool; k++) pool[k] = DhListSlot(k);
        return new VirtualListElement(DhListCount, DhListPitch, pool, first => _dhListFirst = first, DhScrollH)
        {
            VariableHeight = true,
            // Ignored in VariableHeight mode by the 2.30–2.34 framework (only the uniform path pins it); set for intent.
            // The viewport stays DhScrollH because the list panel and the detail panel are built to the same height.
            FixedHeight = true,
            ResetScroll = () => { if (!_dhListReset) return false; _dhListReset = false; return true; },
            RowRevision = DhListRowRevision,
        };
    }

    // Bit 0: a group header on this row · bit 1: the trailing gap (every row but the last). Cheap: no strings.
    private long DhListRowRevision(int i) =>
        (DhGroupKind(i) != DhGrpNone ? 1L : 0L) | (i < DhListCount() - 1 ? 2L : 0L);
}
