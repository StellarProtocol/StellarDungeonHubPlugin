using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeon Confirm — the C# side of the snapshot. The Lua poll (Plugin.DungeonConfirm.Lua.cs) parks ONE delimited string;
// this file parses it into DcSnap and derives every label the window shows, so the window Funcs only read cached fields.
//
// Wire format (US = \x1f between fields, RS = \x1e between records; Lua strips control chars from every field):
//   H  mode(T|M)  dungeonId  twenty(0|1)  totalSec  iAmLeader(0|1)  doing(0|1)  title  diffLabel  artPath  keyLeft(-1)  diffKind  artBgPath  cap  raidContent(0|1)
//      (artBgPath = the raid backdrop drawn under a raid's cut-out art, '' otherwise)
//      (twenty = the 4-group layout, chosen by the TEAM — Lua.cs "layout"; cap = the dungeon's max players, 0 = unknown;
//       raidContent = that dungeon is a raid → the 4-group panel says RAID, else PARTY — Lua.cs "raidContent")
//   A  affixId  name  iconPath                                                  (one per affix)
//   M  charId  name  level  professionId  ai(0|1)  assist(0|1)  ready(0 wait|1 ready|2 declined)  slot20  leader  me  fightPoint(0 = none)  ibs(0 = none)
//      (ibs = Illusion-Breaking Strength, shown "AS (IBS)"; appended last, so an older 12-field record still parses)
public sealed partial class Plugin
{
    private const char DcUs = '\x1f', DcRs = '\x1e';

    private sealed class DcMember
    {
        public string CharId = "", Name = "";
        // Ready: 0 waiting, 1 ready, 2 declined (wire), 3 no answer (C# only, DcMarkTimeouts). Slot: 1..20 raid group
        // slot, 0 = none. Score: ability score, Ibs: Illusion-Breaking Strength — 0 = unknown for both.
        public int    Level, Prof, Ready, Slot, Score, Ibs;
        public bool   Ai, Assist, Leader, Me;
        public string? ScoreText;    // DcScoreText cache …
        public ColorRgba ScoreInk;   // … and the IBS ink it was composed with (theme-live: a theme change rebuilds it)
    }

    private sealed class DcAffix { public int Id; public string Name = "", Icon = "", Label = ""; }

    private sealed class DcSnap
    {
        public bool   Match, Twenty, IAmLeader, Doing;   // Twenty = the 4-group layout (a 20-person team / twenty match)
        public bool   RaidContent;   // the dungeon itself is a raid (cap > 5): 4-group caption RAID, else PARTY
        public int    DungeonId, TotalSec = 30, KeyLeft = -1, Cap;   // Cap: the dungeon's max players, 0 = unknown
        public int    DiffKind;   // header chip colour: 0 none, 1 Normal/Easy, 2 Hard, 3 Master, 4 Nightmare (else muted)
        public string Title = "", TypeName = "", Art = "", ArtBg = "";
        public readonly List<DcAffix>  Affixes = new();
        public readonly List<DcMember> Members = new();

        public int ReadyCount { get { int n = 0; foreach (var m in Members) if (m.Ready == 1) n++; return n; } }
        public int Waiting    { get { int n = 0; foreach (var m in Members) if (m.Ready == 0) n++; return n; } }
        public DcMember? Me   { get { foreach (var m in Members) if (m.Me) return m; return null; } }
        public DcMember? FirstDeclined { get { foreach (var m in Members) if (m.Ready == 2) return m; return null; } }
        public string LeaderName { get { foreach (var m in Members) if (m.Leader) return m.Name; return ""; } }
    }

    // Parse the parked snapshot. Returns null for anything not usable as a window (no header, Lua error record, no
    // members, no title) — the caller's 1.5 s fallback timer treats "null for too long" as "open the game's own view".
    private static DcSnap? DcParse(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.StartsWith("ERR", StringComparison.Ordinal)) return null;
        DcSnap? s = null;
        foreach (var rec in raw.Split(DcRs))
        {
            var f = rec.Split(DcUs);
            if (f.Length == 0) continue;
            switch (f[0])
            {
                case "H" when f.Length >= 11:
                    s = new DcSnap
                    {
                        Match = f[1] == "M", DungeonId = DcInt(f[2]), Twenty = f[3] == "1", TotalSec = DcInt(f[4]),
                        IAmLeader = f[5] == "1", Doing = f[6] == "1", Title = f[7], TypeName = f[8], Art = f[9],
                        KeyLeft = DcInt(f[10], -1), DiffKind = f.Length >= 12 ? DcInt(f[11]) : 0, ArtBg = f.Length >= 13 ? f[12] : "", Cap = f.Length >= 14 ? DcInt(f[13]) : 0,
                        RaidContent = f.Length >= 15 ? f[14] == "1" : f[3] == "1",
                    };
                    if (s.TotalSec <= 0) s.TotalSec = 30;
                    break;
                case "A" when s != null && f.Length >= 4:
                    s.Affixes.Add(new DcAffix { Id = DcInt(f[1]), Name = f[2], Icon = f[3] });
                    break;
                case "M" when s != null && f.Length >= 11:
                    s.Members.Add(new DcMember
                    {
                        CharId = f[1], Name = f[2], Level = DcInt(f[3]), Prof = DcInt(f[4]), Ai = f[5] == "1",
                        Assist = f[6] == "1", Ready = DcInt(f[7]), Slot = DcInt(f[8]), Leader = f[9] == "1", Me = f[10] == "1",
                        Score = f.Length >= 12 ? DcInt(f[11]) : 0,
                        Ibs   = f.Length >= 13 ? DcInt(f[12]) : 0,
                    });
                    break;
            }
        }
        if (s == null || s.Members.Count == 0 || s.Title.Length == 0) return null;
        // Match mode reads the game's player map with pairs() (no stable order) — sort so rows don't shuffle per poll.
        if (s.Match) s.Members.Sort((a, b) => string.CompareOrdinal(a.CharId, b.CharId));
        return s;
    }

    // Lua's tostring() of a number can be "17" or "17.0"; parse both.
    private static int DcInt(string v, int dflt = 0)
    {
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : dflt;
    }

    // ── derived labels ────────────────────────────────────────────────────────────────────────────────────────────
    // Title = the dungeon name only; the difficulty is the chip after it (DcDiffChip, .Window.cs). The native title is
    // "Name  Type" (team_enter_view.lua:259), but its Type is the raw DungeonTypeName — garbage for most rows.
    private string DcTitle() => _dc?.Title ?? "";

    // Status line as (regular, bold, regular) + the bold part's colour KIND (resolved live in the window, so a theme
    // switch repaints): 0 muted, 1 red, 2 green, 3 accent. Cached on a state key: the texts are rebuilt only on a change.
    private (string a, string b, int bKind, string c) _dcStatusCache;
    private string _dcStatusKey = "";

    private (string a, string b, int bKind, string c) DcStatus()
    {
        if (_dc == null) return ("", "", 0, "");
        var s = _dc;
        int waiting = s.Waiting;
        string key = $"{_dcEnd}|{_dcEndName}|{_dcEndCount}|{waiting}|{_dcConfirmed}|{s.Match}|{s.IAmLeader}|{s.LeaderName}";
        if (key == _dcStatusKey) return _dcStatusCache;
        _dcStatusKey = key;
        string more = waiting == 1 ? "1 more" : $"{waiting} more";
        _dcStatusCache = _dcEnd switch
        {
            // No edge spaces: DcStatusLine puts a word-gap spacer between non-empty segments (Window.cs header note).
            DcEnd.Declined  => ("", "Entry cancelled", 1, $"· {_dcEndName} declined"),
            DcEnd.Cancelled => ("", "Entry cancelled", 1, ""),
            // TimedOut (team mode, DcMarkTimeouts): one silent member is named ("You" = me), several are counted.
            DcEnd.TimedOut when _dcEndName.Length > 0 => ("Time ran out", "", 0, $"· {_dcEndName} didn't answer"),
            DcEnd.TimedOut when _dcEndCount > 1       => ("Time ran out", "", 0, $"· {_dcEndCount} didn't answer"),
            DcEnd.TimedOut  => ("Time ran out", "", 0, ""),
            _ when waiting == 0 => ("", "Everyone is ready", 2, ""),
            // The team leader counts as ready from the start (Ready = 1), so test leader before "confirmed".
            _ when !s.Match && s.IAmLeader => ($"You started entry · waiting for {waiting}", "", 0, ""),
            _ when _dcConfirmed => ("", "You're ready", 2, $"· waiting for {more}"),
            _ when s.Match      => ("Match found · confirm to enter", "", 0, ""),
            _                   => ("Leader", s.LeaderName, 3, "started entry"),
        };
        return _dcStatusCache;
    }

    // Affix chips sit in ONE row of the 624 px panel and can't wrap, and real affix names are long ("Void Corruption
    // Afterimage"). A chip hugs its label, so the label is pre-cut here to an equal share of the row: estimated glyph
    // widths for the bold chip label (the prototype's bold 12: ≈7 px Latin, ≈12 px CJK/other, × DcScale), cut with "…"
    // (both TMP faces carry U+2026). Row / key-chip / chip-chrome widths: the constants block in .Window.cs.
    private static void DcFitAffixLabels(DcSnap s)
    {
        int n = Math.Min(s.Affixes.Count, DcMaxAffixes);
        if (n == 0) return;
        float row = DcAffixRowW - (s.KeyLeft >= 0 ? DcKeyChipW + DcChipGap : 0f) - DcChipGap * (n - 1);
        float budget = Math.Max(24f * DcScale, row / n - DcChipChrome);
        foreach (var a in s.Affixes) a.Label = DcFit(a.Name, budget);
    }

    private static string DcFit(string text, float px)
    {
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            w += (text[i] < 0x2E80 ? 7f : 12f) * DcScale;
            if (w > px) return text.Substring(0, Math.Max(1, i - 1)).TrimEnd() + "…";
        }
        return text;
    }

    // ── raid grid ─────────────────────────────────────────────────────────────────────────────────────────────────
    // 20 cells (group g = cell / 5). A member with a valid team group slot (team_vm GetTeamTwentyIndex = (groupId-1)*5 +
    // index, from baseInfo.teamMemberGroupInfos) sits in that cell; the rest (match mode has no groups) fill the free
    // cells in list order. Rebuilt on every snapshot change.
    private readonly int[] _dcGrid = new int[20];

    private void DcBuildGrid(DcSnap s)
    {
        for (int i = 0; i < 20; i++) _dcGrid[i] = -1;
        for (int i = 0; i < s.Members.Count; i++)
        {
            int slot = s.Members[i].Slot - 1;
            if (slot >= 0 && slot < 20 && _dcGrid[slot] < 0) _dcGrid[slot] = i;
            else s.Members[i].Slot = 0;
        }
        int next = 0;
        for (int i = 0; i < s.Members.Count; i++)
        {
            if (s.Members[i].Slot > 0) continue;
            while (next < 20 && _dcGrid[next] >= 0) next++;
            if (next >= 20) break;
            _dcGrid[next] = i;
        }
    }
}
