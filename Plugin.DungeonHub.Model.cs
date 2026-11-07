using System;
using System.Collections.Generic;

namespace Stellar.DungeonHub;

// Dungeons hub — the C# side of the Lua snapshot (Plugin.DungeonHub.Lua.cs writes it) and everything derived from it:
// the current tab's list, the selected entry / difficulty, and what each "how to go" button may do right now.
//
// Records (fields joined by US = char 31, records by RS = char 30, the Dungeon Confirm separators):
//   T  live: inTeam, leader, count, max, matchType, matchDungeon, matchDiff, matchAct, elapsedSec, matchName,
//      matchDiffName, inDungeon, myScore, openState
//   E  list entry: tab, key, name, art, artBg, locked, sub, cap, group, masterId, masterCount, masterMax, masterArt,
//      funcId, counterKey, counterVal, counterZero, canMatch, matchActId [, unstable, title, masterOn]
//   V  difficulty of an entry (hero 1 Normal / 2 Hard, an Unstable entry only 0, raid 0 Easy / 1 Hard / 2 Nightmare, a
//      Backtrack raid only 0): key, index, dungeonId, diff, label, unlocked, rec, hasSolo, soloOk, soloWhy, match,
//      counterKey, counterVal, counterZero, art, artBg, kind (hero star 1/2, raid 1/2/4, Unstable 8)
//      [, recText (Unstable: the game's text instead of rec), firstClear (-1 n/a, 0 none, 1 ready, 2 claimed)]
//   M  one Master level of the SELECTED hero dungeon: diff, dungeonId, label, unlocked, rec, hasSolo, soloOk, soloWhy,
//      match, counterKey, counterVal, counterZero, cleared (diff <= GetMasterDungeonMaxDiff)
//   W  World Boss: open, left, before, opensText, bossName, art, bossCfgId, haveInfo, log (.WorldBoss.cs)
//   G  Guild Hunt: inGuild, open, left, before, actRem, actLim, art, (id, rec, rem, lim) × Normal/Hard (.GuildHunt.cs)
//   C  Bureau Commissions: haveQuest, acceptLeft, total, cap, resetSec, resetHow, n, (id, name, area, done, dungeonId) × n
//      (.Commissions.cs)
// E/V come only with a FULL poll (catalog, every DhFullEvery polls or on demand); T and M come with every poll.
public sealed partial class Plugin
{
    private const int DhTabCount = 5, DhTabHero = 0, DhTabRaid = 1;
    // Hero level slots: 1 Normal · 2 Hard · 3 Master (= the page's StarLevel); slot 0 is used only by an Unstable entry
    // (v5: Unstable is its own list entry with that one level, no longer a chip on the regular dungeon).
    private const int DhChipCount = 4, DhUnstableIdx = 0, DhMasterIdx = 3;
    private static readonly string[] DhTabNames = { "Dungeons", "Raid", "World Boss", "Hunt", "Other" };

    private sealed class DhLevel
    {
        public int DungeonId, Diff, Rec, Kind;   // Diff = Master difficulty (0 for Normal/Hard and raids); Kind 8 = Unstable
        public int FirstClear = -1;              // hero Normal/Hard: 0 no first-clear reward, 1 ready, 2 claimed; -1 n/a
        public string Label = "", SoloWhy = "", CounterKey = "", CounterVal = "", Art = "", ArtBg = "", RecText = "";
        public string SubText = "", TileVal = "";   // list sub-line / info-tile value, built once at parse (DhCounterText)
        public bool Unlocked, HasSolo, SoloOk, Match, CounterZero, Cleared;
    }

    private sealed class DhEntry
    {
        public int Tab, Cap, Group, MasterId, MasterCount, MasterMax, FuncId, MatchActId;
        public string Key = "", Name = "", Art = "", ArtBg = "", Sub = "", MasterArt = "", CounterKey = "", CounterVal = "", CapText = "";
        public string SubText = "", TileVal = "";   // activities: the list sub-line / tile value (DhCounterText)
        public string Title = "";                   // detail title when it differs from the list name (Unstable: full name)
        public bool Locked, CounterZero, CanMatch, Unstable;
        public bool MasterOn;                       // the Master dungeon is unlocked (function on + IsUnlockDungeonId)
        public string? HighSub;                     // DhRowSub's cache (entries are rebuilt on every catalog parse)
        public readonly DhLevel?[] Levels = new DhLevel?[DhChipCount];   // hero: Unstable, Normal, Hard (3 = Master, polled
                                                                          // as M rows); raid: Easy, Hard, Nightmare
        public bool IsActivity => Tab > DhTabRaid;
    }

    private sealed class DhLiveState
    {
        public bool InTeam, Leader, InDungeon;
        public int Count, Max = 5, MatchType, MatchDungeon, MatchDiff, MatchAct, MyScore;
        public double Elapsed, ElapsedAt;   // seconds matching at the poll + the TimeNow of that poll
        public string MatchName = "", MatchDiffName = "", OpenState = "";
        public bool Matching => MatchType != 0;
    }

    private readonly List<DhEntry>[] _dhLists = { new(), new(), new(), new(), new() };
    private readonly List<DhLevel> _dhMaster = new();
    private DhLiveState _dhLive = new();
    private bool _dhHaveCatalog;

    private void DhParse(string raw)
    {
        bool full = false;
        var lists = new List<DhEntry>[DhTabCount];
        var byKey = new Dictionary<string, DhEntry>();
        _dhMaster.Clear();
        foreach (var rec in raw.Split(DcRs))
        {
            var f = rec.Split(DcUs);
            switch (f[0])
            {
                case "T" when f.Length >= 15:
                    _dhLive = new DhLiveState
                    {
                        InTeam = f[1] == "1", Leader = f[2] == "1", Count = DcInt(f[3]), Max = Math.Max(1, DcInt(f[4], 5)),
                        MatchType = DcInt(f[5]), MatchDungeon = DcInt(f[6]), MatchDiff = DcInt(f[7]), MatchAct = DcInt(f[8]),
                        Elapsed = DcInt(f[9]), ElapsedAt = DcNow, MatchName = f[10], MatchDiffName = f[11],
                        InDungeon = f[12] == "1", MyScore = DcInt(f[13]), OpenState = f[14],
                    };
                    break;
                case "E" when f.Length >= 20:
                {
                    if (!full) { full = true; for (int i = 0; i < DhTabCount; i++) lists[i] = new List<DhEntry>(); }
                    int tab = DcInt(f[1]);
                    if (tab < 0 || tab >= DhTabCount) break;
                    var e = new DhEntry
                    {
                        Tab = tab, Key = f[2], Name = f[3], Art = f[4], ArtBg = f[5], Locked = f[6] == "1", Sub = f[7],
                        Cap = DcInt(f[8]), Group = DcInt(f[9]), MasterId = DcInt(f[10]), MasterCount = DcInt(f[11]),
                        MasterMax = DcInt(f[12]), MasterArt = f[13], FuncId = DcInt(f[14]), CounterKey = f[15],
                        CounterVal = f[16], CounterZero = f[17] == "1", CanMatch = f[18] == "1", MatchActId = DcInt(f[19]),
                    };
                    if (f.Length > 20) e.Unstable = f[20] == "1";
                    if (f.Length > 21) e.Title = f[21];
                    if (f.Length > 22) e.MasterOn = f[22] == "1";
                    if (e.Cap > 0) e.CapText = $"{e.Cap}-player";
                    (e.SubText, e.TileVal) = DhCounterText(e.CounterVal, e.CounterZero);
                    lists[tab].Add(e);
                    byKey[e.Key] = e;
                    break;
                }
                case "V" when f.Length >= 18 && byKey.TryGetValue(f[1], out var owner):
                {
                    int idx = DcInt(f[2], -1);
                    if (idx < 0 || idx >= owner.Levels.Length) break;
                    var lv = new DhLevel
                    {
                        DungeonId = DcInt(f[3]), Diff = DcInt(f[4]), Label = f[5], Unlocked = f[6] == "1", Rec = DcInt(f[7]),
                        HasSolo = f[8] == "1", SoloOk = f[9] == "1", SoloWhy = f[10], Match = f[11] == "1",
                        CounterKey = f[12], CounterVal = f[13], CounterZero = f[14] == "1", Art = f[15], ArtBg = f[16],
                        Kind = DcInt(f[17]),
                    };
                    if (f.Length > 18) lv.RecText = f[18];
                    if (f.Length > 19) lv.FirstClear = DcInt(f[19], -1);
                    if (lv.FirstClear >= 0)
                    {
                        // Normal / Hard: no reward counter in the game — the first-clear reward, like the hero page
                        lv.SubText = lv.FirstClear switch
                        {
                            1 => $"{lv.Label} · first clear ready", 2 => $"{lv.Label} · first clear claimed", _ => lv.Label,
                        };
                        lv.TileVal = lv.CounterVal;
                    }
                    else (lv.SubText, lv.TileVal) = DhCounterText(lv.CounterVal, lv.CounterZero);
                    // Raid rewards are counted per difficulty: the tile names the picked one ("Rewards left · Easy").
                    if (owner.Tab == DhTabRaid && lv.CounterKey.Length > 0 && lv.Label.Length > 0)
                        lv.CounterKey = $"{lv.CounterKey} · {lv.Label}";
                    owner.Levels[idx] = lv;
                    break;
                }
                case "M" when f.Length >= 14:
                {
                    var lv = new DhLevel
                    {
                        Diff = DcInt(f[1]), DungeonId = DcInt(f[2]), Label = f[3], Unlocked = f[4] == "1", Rec = DcInt(f[5]),
                        HasSolo = f[6] == "1", SoloOk = f[7] == "1", SoloWhy = f[8], Match = f[9] == "1",
                        CounterKey = f[10], CounterVal = f[11], CounterZero = f[12] == "1", Cleared = f[13] == "1", Kind = 4,
                    };
                    lv.TileVal = lv.CounterVal;   // "First clear": Cleared / Reward ready, never a count
                    _dhMaster.Add(lv);
                    break;
                }
                case "W" when f.Length >= 10: DhParseWb(f); break;   // World Boss timer + this week's boss (.WorldBoss.cs)
                case "G" when f.Length >= 16: DhParseGh(f); break;   // Guild Hunt guild / window / counters (.GuildHunt.cs)
                case "C" when f.Length >= 8: DhParseCm(f); break;    // Bureau Commissions quest / events (.Commissions.cs)
            }
        }
        if (full)
        {
            for (int i = 0; i < DhTabCount; i++) _dhLists[i].Clear();
            for (int i = 0; i < DhTabCount; i++) _dhLists[i].AddRange(lists[i]);
            if (!_dhHaveCatalog)
                _services.Log.Info($"[DungeonHub] catalog: {_dhLists[0].Count} dungeons, {_dhLists[1].Count} raids, " +
                    $"{_dhLists[2].Count + _dhLists[3].Count + _dhLists[4].Count} activities");
            _dhHaveCatalog = true;
        }
        _dhMaster.Sort((a, b) => a.Diff.CompareTo(b.Diff));
        DhBuildLiveText();
        DhFixSelection();
    }

    // "Rewards left x / y" is information only: at 0 it reads "0 / y · no rewards" (muted, never red) and never
    // disables a button — entering with no rewards left is the player's call. Built once per parse, read per frame.
    private static (string sub, string tile) DhCounterText(string val, bool zero) =>
        val.Length == 0 ? ("", "") : ($"Rewards left {val}", zero ? $"{val} · no rewards" : val);

    // ── selection ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Each entry remembers its own difficulty and Master level (_dhPicks), so coming back to a dungeon shows what was
    // picked there, and the list row's "Rewards left" follows that entry's pick, not the current one's.
    private sealed class DhPick { public int Idx, MasterDiff; }
    private readonly Dictionary<string, DhPick> _dhPicks = new();

    private int _dhTab;
    private readonly string[] _dhSelKey = { "", "", "", "", "" };
    private int _dhLevelIdx;            // hero: 0 Unstable, 1 Normal, 2 Hard, 3 Master · raid: 0 Easy, 1 Hard, 2 Nightmare
    private int _dhMasterDiff;          // the Master level picked in the grid (1-based); 0 = not chosen yet
    private string _dhLevelFor = "";    // the entry key _dhLevelIdx / _dhMasterDiff belong to

    private List<DhEntry> DhList => _dhLists[_dhTab];

    private DhEntry? DhCur
    {
        get
        {
            var list = DhList;
            foreach (var e in list) if (e.Key == _dhSelKey[_dhTab]) return e;
            return list.Count > 0 ? list[0] : null;
        }
    }

    private DhEntry? DhAt(int i) => i < DhList.Count ? DhList[i] : null;

    // A new entry starts on Normal (hero) or its first difficulty (raid: a Backtrack group has only one), and its
    // Master grid on the game's own default level (hero_dungeon_main_view initMasterDiffLoop: highest cleared + 1,
    // capped at the list).
    private static int DhDefaultIdx(DhEntry e)
    {
        if (e.Tab == DhTabHero && e.Levels[1] != null) return 1;
        for (int i = 0; i < DhMasterIdx; i++) if (e.Levels[i] != null) return i;
        return e.Tab == DhTabHero && e.MasterCount > 0 ? DhMasterIdx : 0;
    }

    private DhPick DhPickOf(DhEntry e)
    {
        if (!_dhPicks.TryGetValue(e.Key, out var p)) _dhPicks[e.Key] = p = new DhPick { Idx = DhDefaultIdx(e) };
        // A remembered slot that no longer has a level (v4's Unstable chip 0 on a regular dungeon, a difficulty gone
        // from the catalog) falls back to the entry's default.
        else if (!DhHasLevel(e, p.Idx)) p.Idx = DhDefaultIdx(e);
        if (p.MasterDiff == 0 && e.MasterCount > 0) p.MasterDiff = Math.Clamp(e.MasterMax + 1, 1, e.MasterCount);
        return p;
    }

    // Keep the selection on a real entry and load that entry's remembered pick.
    private void DhFixSelection()
    {
        var cur = DhCur;
        if (cur == null) return;
        _dhSelKey[_dhTab] = cur.Key;
        var p = DhPickOf(cur);
        if (_dhLevelFor != cur.Key)
        {
            _dhLevelFor = cur.Key;
            _dhFullDue = true;   // the Master rows follow the selection
            _dhMaster.Clear();   // they belonged to the previous dungeon; the next poll brings this one's
        }
        _dhLevelIdx = p.Idx;
        _dhMasterDiff = p.MasterDiff;
    }

    private static bool DhHasLevel(DhEntry e, int idx) => idx >= 0 && idx < DhChipCount &&
        (idx == DhMasterIdx ? e.Tab == DhTabHero && e.MasterCount > 0 : e.Levels[idx] != null);

    private bool DhIsMaster => _dhTab == DhTabHero && _dhLevelIdx == DhMasterIdx;
    private bool DhIsUnstable => _dhTab == DhTabHero && DhCur is { Unstable: true };

    private DhLevel? DhCurLevel
    {
        get
        {
            var e = DhCur;
            if (e == null || e.IsActivity) return null;
            if (DhIsMaster) { foreach (var l in _dhMaster) if (l.Diff == _dhMasterDiff) return l; return null; }
            return e.Levels[Math.Clamp(_dhLevelIdx, 0, DhChipCount - 1)];
        }
    }

    // The list row's sub-line: Locked; an Unstable entry's "Rewards left"; a regular hero dungeon's HIGHEST unlocked
    // difficulty, whatever is picked (user 2026-11-07: "<pick> · first clear ready" read as confusing) — "Master N" with
    // N = the grid's default level (GetMasterDungeonMaxDiff + 1, capped; "Master 20 · all cleared" once every level is
    // cleared), else "Hard", else "Normal"; a raid with 2+ difficulties: every open difficulty's count (DhRaidRowSub),
    // a one-difficulty raid (Backtrack): "Rewards left" for its pick; an activity's own line.
    private string DhRowSub(DhEntry e)
    {
        if (e.Locked) return "Locked";
        if (e.IsActivity) return DhIsWb(e) ? DhWbRowSub(e) : DhIsGh(e) ? DhGhRowSub(e) : DhIsCm(e) ? DhCmRowSub(e) : e.SubText;
        if (e.Tab == DhTabHero && !e.Unstable)
        {
            if (e.HighSub != null) return e.HighSub;
            if (e.MasterCount > 0 && e.MasterOn)
            {
                int n = Math.Clamp(e.MasterMax + 1, 1, Math.Min(e.MasterCount, DhMasterLabels.Length));
                return e.HighSub = e.MasterMax >= e.MasterCount ? $"{DhMasterLabels[n - 1]} · all cleared" : DhMasterLabels[n - 1];
            }
            return e.HighSub = e.Levels[2] is { Unlocked: true } ? "Hard" : "Normal";
        }
        if (e.Tab == DhTabRaid && (e.HighSub ??= DhRaidRowSub(e) ?? "").Length > 0) return e.HighSub;
        var l = e.Levels[Math.Clamp(DhPickOf(e).Idx, 0, DhChipCount - 1)];
        return l?.SubText ?? e.Sub;
    }

    // Raid rewards are counted per difficulty (sum over its BossId of raid:GetBossCountId), so one difficulty's
    // "Rewards left 3 / 3" on the row was misleading. A 2+-difficulty group lists every OPEN difficulty's remaining
    // count in its chip colour ("Easy 3 · Hard 3 · Nightm. 0"); closed ones are omitted (the chip already says so).
    // One TMP text with <color> tags — the row's sub-line is a RichEmoji TextElement, i.e. TMP with richText on (the
    // legacy-Text fallback also parses <color>); untagged separators keep the element's theme-live muted colour.
    // 0 left = the same colour at ~55 % alpha: info only, never red, never disables. "Nightm." only when all three
    // show — the full word overflows the 226 px text column at 18 px. Null = not a multi-difficulty group / no counts.
    private static readonly string[] DhRaidHex = { "5fd38d", "ffcc73", "b385ff" };   // DhGreen / DhAmber / DhPurple
    private static string? DhRaidRowSub(DhEntry e)
    {
        int have = 0, shown = 0;
        for (int i = 0; i < 3; i++)
        {
            if (e.Levels[i] == null) continue;
            have++;
            if (e.Levels[i] is { Unlocked: true, CounterVal.Length: > 0 }) shown++;
        }
        if (have < 2 || shown == 0) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            if (e.Levels[i] is not { Unlocked: true, CounterVal.Length: > 0 } l) continue;
            int slash = l.CounterVal.IndexOf('/');
            string rem = (slash > 0 ? l.CounterVal[..slash] : l.CounterVal).Trim();
            string label = i == 2 && shown == 3 ? "Nightm." : l.Label;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append("<color=#").Append(DhRaidHex[i]).Append(l.CounterZero ? "8c" : "").Append('>')
              .Append(label).Append(' ').Append(rem).Append("</color>");
        }
        return sb.ToString();
    }

    private void DhSelectEntry(int i)
    {
        var e = DhAt(i);
        if (e == null || e.Key == _dhSelKey[_dhTab]) return;
        _dhSelKey[_dhTab] = e.Key;
        DhFixSelection();
    }

    private void DhSelectTab(int tab)
    {
        if (tab == _dhTab) return;
        _dhTab = tab;
        _cfg.Set<int>("dungeon_hub_tab", tab);
        _cfg.Save();
        _dhLevelFor = "";
        DhFixSelection();
        if (tab == DhTabWb) DhWbFetch();   // this week's boss (also needed before matching)
    }

    private void DhSelectLevel(int idx)
    {
        var e = DhCur;
        if (e == null || e.IsActivity || !DhHasLevel(e, idx)) return;
        _dhLevelIdx = idx;
        DhPickOf(e).Idx = idx;
    }

    // Master grid: a locked level is not clickable (the game snaps a locked pick back to max+1; we just ignore it).
    private void DhSelectMaster(int diff)
    {
        var e = DhCur;
        var l = diff >= 1 && diff <= _dhMaster.Count ? _dhMaster[diff - 1] : null;
        if (e == null || l == null || !l.Unlocked) return;
        _dhMasterDiff = diff;
        _dhLevelIdx = DhMasterIdx;
        var p = DhPickOf(e);
        p.MasterDiff = diff;
        p.Idx = DhMasterIdx;
    }

    // ── live text ─────────────────────────────────────────────────────────────────────────────────────────────────
    private double DhElapsed => _dhLive.Matching ? _dhLive.Elapsed + Math.Max(0, DcNow - _dhLive.ElapsedAt) : 0;

    private string _dhClock = ""; private int _dhClockSec = -1;
    private string DhClock()
    {
        int s = (int)DhElapsed;
        if (s != _dhClockSec) { _dhClockSec = s; _dhClock = $"{s / 60:00}:{s % 60:00}"; }
        return _dhClock;
    }

    // Header status: three segments (muted · bold accent · muted), like Dungeon Confirm's status line, and the bottom
    // bar's party text — built once per poll (DhParse → DhBuildLiveText), read by the per-frame Funcs.
    private (string a, string b, string c) _dhStatus = ("Solo", "", "");
    private string _dhPartyText = "Not in a party";

    private (string a, string b, string c) DhStatus() => _dhStatus;
    private string DhPartyText() => _dhPartyText;

    private void DhBuildLiveText()
    {
        var l = _dhLive;
        _dhStatus = !l.InTeam ? ("Solo", "", "") : ("In party", $"{l.Count}/{l.Max}", l.Leader ? "· leader" : "· member");
        _dhPartyText = !l.InTeam ? "Not in a party" : $"Party {l.Count}/{l.Max} · {(l.Leader ? "you are leader" : "member")}";
    }

    // "· <dungeon> (<difficulty>) · mm:ss", rebuilt only when the clock ticks or the poll replaced the live state.
    private string _dhMatchText = ""; private int _dhMatchTextSec = -1; private DhLiveState? _dhMatchTextFor;
    private string DhMatchText()
    {
        var l = _dhLive; var clock = DhClock();
        if (_dhClockSec == _dhMatchTextSec && ReferenceEquals(l, _dhMatchTextFor)) return _dhMatchText;
        _dhMatchTextSec = _dhClockSec; _dhMatchTextFor = l;
        string what = l.MatchDiffName.Length > 0 ? $"{l.MatchName} ({l.MatchDiffName})" : l.MatchName;
        return _dhMatchText = what.Length > 0 ? $"· {what} · {clock}" : $"· {clock}";
    }

    // Is the live match for this exact selection?
    private bool DhMatchingThis()
    {
        var l = _dhLive; var e = DhCur;
        if (!l.Matching || e == null) return false;
        if (e.IsActivity) return l.MatchType == 2 && e.MatchActId > 0 && l.MatchAct == e.MatchActId;
        var lv = DhCurLevel;
        return l.MatchType == 1 && lv != null && l.MatchDungeon == lv.DungeonId && l.MatchDiff == lv.Diff;
    }
}
