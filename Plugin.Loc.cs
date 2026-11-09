using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Localization (CLAUDE.md Rule 10): every player-visible string goes through the framework catalog —
// Lang/{en,ja,ko,th,id,fil}.json, embedded in the csproj, keys "dh.*" (hub) and "dc.*" (confirm + its settings).
//   L(key)        — a plain catalog string.
//   LF(key, …)    — a template with positional {0}/{1} placeholders (other languages may reorder them).
// Live language switch: the UI reads text through per-frame Func<string> labels, so most of it follows at once. Text
// composed at PARSE time (list sub-lines, tile labels — DhParse) follows on the next catalog poll (≤ 3 s while open);
// the cached status lines (DhBuildLiveText, DcStatus) key their cache on the language. Window / launcher titles use
// TitleProvider; the hotkey and Esc-rail labels are fixed at registration (no provider in the SDK).
//
// Two things the Lua side writes in ENGLISH are mapped here, so the Lua (and the "[DungeonHub] …" log lines, which
// stay English for us) are unchanged:
//   DhWord(s)   — the fixed words the snapshot carries (difficulty names, "Locked", "Rewards left", …); anything else
//                 (the game's own already-localized text) passes through.
//   DhReason(s) — the reason after "NO " of an enter / match / accept chunk (Enter.cs, GuildHunt.cs, Commissions.cs).
//                 ⚠️ When a Lua 'NO …' text changes, change its key here too — an unmapped one shows in English.
public sealed partial class Plugin
{
    private ILocalization _loc = null!;   // set first thing in the ctor (Plugin.cs), before any window is built

    private string L(string key) => _loc.T(key);
    private string LF(string key, params object[] args) => _loc.TFormat(key, args);

    private static readonly Dictionary<string, string> DhWordKeys = new(StringComparer.Ordinal)
    {
        ["Normal"] = "dh.diff.normal", ["Hard"] = "dh.diff.hard", ["Easy"] = "dh.diff.easy",
        ["Nightmare"] = "dh.diff.nightmare", ["Unstable"] = "dh.diff.unstable",
        ["Locked"] = "dh.word.locked", ["Rewards left"] = "dh.word.rewardsLeft", ["First clear"] = "dh.word.firstClear",
        ["Reward ready"] = "dh.word.rewardReady", ["Claimed"] = "dh.word.claimed", ["Cleared"] = "dh.word.cleared",
    };

    private string DhWord(string? s) => s == null ? "" : DhWordKeys.TryGetValue(s, out var k) ? _loc.T(k) : s;

    private static readonly Dictionary<string, string> DhReasonKeys = new(StringComparer.Ordinal)
    {
        ["not allowed here right now"] = "dh.why.notHere",
        ["this Master level is locked"] = "dh.why.masterLocked",
        ["this difficulty is locked"] = "dh.why.diffLocked",
        ["no solo on this level"] = "dh.why.noSoloLevel",
        ["no solo on this dungeon"] = "dh.why.noSoloDungeon",
        ["solo not unlocked yet"] = "dh.why.soloLocked",
        ["leader only"] = "dh.why.leaderOnly",
        ["this raid is closed"] = "dh.why.raidClosed",
        ["no boss open on this difficulty"] = "dh.why.noBoss",
        ["the daily quest is already accepted"] = "dh.why.questAccepted",
        ["no accepts left"] = "dh.why.noAccepts",
        ["Not in a guild"] = "dh.why.notInGuild",
        ["Guild Hunt is locked"] = "dh.why.ghLocked",
        ["You are in a party - use Party"] = "dh.why.youInParty",
        ["Not in a party - use Solo"] = "dh.why.notInPartySolo",
        ["Guild Hunt needs a party"] = "dh.why.ghNeedsParty",
    };

    // The parametrised ones: 'party too big for solo (6 > 5)', 'needs 5+ in the party (you have 3) - use Match',
    // 'Not in your guild: <names>' (Enter.cs / GuildHunt.cs Lua).
    private static readonly Regex DhWhyBig = new(@"^party too big for solo \((\d+) > (\d+)\)$", RegexOptions.CultureInvariant);
    private static readonly Regex DhWhyNeeds = new(@"^needs (\d+)\+ in the party \(you have (\d+)\) - use Match$", RegexOptions.CultureInvariant);
    private const string DhWhyGuildPrefix = "Not in your guild: ";

    private string DhReason(string why)
    {
        if (DhReasonKeys.TryGetValue(why, out var k)) return L(k);
        var m = DhWhyBig.Match(why);
        if (m.Success) return LF("dh.why.partyBig", m.Groups[1].Value, m.Groups[2].Value);
        m = DhWhyNeeds.Match(why);
        if (m.Success) return LF("dh.why.needs", m.Groups[1].Value, m.Groups[2].Value);
        if (why.StartsWith(DhWhyGuildPrefix, StringComparison.Ordinal)) return LF("dh.why.notYourGuild", why.Substring(DhWhyGuildPrefix.Length));
        return why;
    }

    // Countdown "1d 16h 25m" / "16h 25m" / "25m 03s" / "45s" (World Boss, Guild Hunt; days = false for Commissions'
    // reset, which is under a day). Callers cache per second.
    private string DhDur(int s, bool days = true)
    {
        int d = days ? s / 86400 : 0, h = (days ? s % 86400 : s) / 3600, m = s % 3600 / 60, sec = s % 60;
        return d > 0 ? LF("dh.dur.dhm", d, h, m) : h > 0 ? LF("dh.dur.hm", h, m) : m > 0 ? LF("dh.dur.ms", m, sec) : LF("dh.dur.s", sec);
    }

    // "Master N" labels (chip + list sub-line), cached per language: the chip's label Func runs every frame.
    private string[]? _dhMasterLabels;
    private string _dhMasterLabelsLang = "";

    private string DhMasterLabel(int n)
    {
        if (_dhMasterLabels == null || _dhMasterLabelsLang != _loc.Language)
        {
            _dhMasterLabelsLang = _loc.Language;
            var a = new string[DhLvCount];
            for (int i = 0; i < a.Length; i++) a[i] = LF("dh.diff.masterN", i + 1);
            _dhMasterLabels = a;
        }
        return _dhMasterLabels[Math.Clamp(n, 1, DhLvCount) - 1];
    }
}
