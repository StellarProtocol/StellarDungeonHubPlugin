using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeons hub — warm-up after entering the world, so the FIRST open doesn't freeze the game (user report 2026-11-08;
// later opens were always fine). What a first open paid all at once, and what is now done ahead:
//   1. CATALOG — the first FULL poll walks every dungeon / raid / activity config table (GetDatas, GetRow) and the
//      game loads a table on its first touch. Run one FULL poll hidden → the open's own FULL poll finds them loaded.
//      Its entry names are handed to IWindowHost.PrewarmText so their glyphs rasterize a few per frame in advance.
//   2. GLYPHS — the header's ✕ and ⚙ PNGs (DcGlyphPng, both colours) are baked now instead of inside the build.
//   3. ART — EVERY tab's list thumbs (the NPC tab's full list, not the searched view) + the selected entry's band, loaded
//      AND composed (raid pairs, NPC thumbs / band): composed RTs are kept until logout now (.ArtCache.cs), so the first
//      open — and every tab switch after it — finds them ready instead of composing them one per frame in view.
//      Rationed exactly like an open: DhLoad's per-frame request budget and DhComposeAllowed (one compose per frame =
//      one per tick here). Each tick calls up to DhWarmArtPerTick thumb Funcs round-robin (a ready one is a dictionary
//      hit); done once every entry answered ready in one unbroken run, or after DhWarmArtMaxSec (a path that never
//      loads must not keep the stage alive).
// The window itself is NOT mounted here any more: WindowSpec.Prebuild (fw 2.30.0, DhRegister) builds the tree at login
// and fills it off-screen in the world — Conditional branches and list rows included, which the old hidden
// SetVisible(true)/(false) mount/park stage never reached (a hidden window never Applies). The catalog this warm-up
// reads changes the hub's PrebuildSignature, so the framework re-fills it after stage 1.
// One stage per poll tick (DhPollSec = 0.5 s), so no single frame does more than one. Starts DhWarmDelaySec after the
// player is in the world, not loading, Lua up; restarts that wait on a loading screen. Any real open ends the warm-up
// (the open does the rest itself). Re-armed on logout (the next character's catalog differs).
public sealed partial class Plugin
{
    private const double DhWarmDelaySec = 8.0;
    private const int DhWarmArtPerTick = 12;
    private const double DhWarmArtMaxSec = 60.0;

    private int    _dhWarmStage;        // 0 waiting · 1 catalog · 2 glyphs · 3 art · 4 done
    private double _dhWarmSince = -1;   // DcNow the world first looked settled (-1 = not yet / reset by loading)
    private int    _dhWarmArtAt;        // stage 3: next entry (flat index over every tab's list), round-robin
    private int    _dhWarmArtRun;       // stage 3: entries answered ready in a row (≥ the total = all ready)
    private double _dhWarmArtSince = -1;   // stage 3: DcNow it started (the DhWarmArtMaxSec cap)

    private const int DhWarmDone = 4;

    private void DhWarmTick()
    {
        if (_dhWarmStage >= DhWarmDone) return;
        if (_dhShow || _dhOpening) { DhWarmEnd(); return; }
        bool ready = _services.ClientState.Phase == GamePhase.World && _services.Lua.Ready &&
                     (_services.ClientState.UiState & GameUIState.Loading) == 0;
        if (_dhWarmStage == 0)
        {
            if (!ready) { _dhWarmSince = -1; return; }
            if (_dhWarmSince < 0) { _dhWarmSince = DcNow; return; }
            if (DcNow - _dhWarmSince < DhWarmDelaySec) return;
            _dhWarmStage = 1;
        }
        if (!ready) return;   // a loading screen mid-way: hold the stage, carry on after it
        switch (_dhWarmStage)
        {
            case 1:   // catalog: one FULL poll while hidden
                DhPoll(warm: true);
                if (!_dhHaveCatalog) return;   // poll failed / snapshot not in yet → retry next tick
                for (int t = 0; t < DhTabCount; t++)
                    foreach (var e in _dhLists[t])
                    {
                        _services.Windows.PrewarmText(e.Name); _services.Windows.PrewarmText(e.SubText);
                        if (e.Services != null) foreach (var s in e.Services) _services.Windows.PrewarmText(s.Name);   // NPC tab
                    }
                break;
            case 2:   // glyphs: the header ✕ (muted / hover red) and ⚙ (muted / hover accent), as Window.cs / Settings.cs bake them
            {
                float glyph = DcSE(12f);
                DcGlyphPng(DcGlyph.Cross, glyph, MutedColor()); DcGlyphPng(DcGlyph.Cross, glyph, DhRed);
                DcGlyphPng(DcGlyph.Gear, glyph, MutedColor()); DcGlyphPng(DcGlyph.Gear, glyph, Menu.MenuAccent);
                break;
            }
            case 3:   // art: every tab's thumbs + the band, loads and composes, a slice per tick
                if (!DhWarmArt()) return;   // more next tick
                break;
        }
        if (++_dhWarmStage >= DhWarmDone) DhWarmEnd();
    }

    // One slice of stage 3; true = done. "Ready" = the box would draw its final art now: the Func returned a texture,
    // or the entry has no art at all. A Func answering null is still loading / waiting for its compose turn.
    // Off the hot path (2 Hz, hidden, ≤ DhWarmArtPerTick calls); the one log line is per login.
    private bool DhWarmArt()
    {
        if (_dhWarmArtSince < 0) _dhWarmArtSince = DcNow;
        int total = 0;
        for (int t = 0; t < DhTabCount; t++) total += _dhLists[t].Count;
        bool band = DhCur == null || DhBandTex() != null;   // the saved tab's selected entry (first in line for a compose)
        for (int n = 0; n < DhWarmArtPerTick && total > 0 && _dhWarmArtRun < total; n++)
        {
            int at = _dhWarmArtAt++ % total, t = 0;
            while (at >= _dhLists[t].Count) at -= _dhLists[t++].Count;
            var e = _dhLists[t][at];
            // A raid pair shows its bare backdrop while the cut-out loads, so it is ready only once composed (or failed).
            bool ready = (!e.IsNpc && DhArtOf(e).Length == 0) || (DhThumbTexOf(e) != null &&
                         (e.IsNpc || e.ArtBg.Length == 0 || _dhRaidComp.ContainsKey((e.ArtBg, DhArtOf(e)))));
            _dhWarmArtRun = ready ? _dhWarmArtRun + 1 : 0;
        }
        bool done = band && _dhWarmArtRun >= total;
        if (!done && DcNow - _dhWarmArtSince < DhWarmArtMaxSec) return false;
        _services.Log.Info($"[DungeonHub] warm-up art {(done ? "ready" : "stopped at the time cap")}: {total} entries, " +
                           $"{DcNow - _dhWarmArtSince:0.0} s, {_dhRaidComp.Count + _dhComp.Count} composed");
        return true;
    }

    // Done, or a real open came first (it does the rest itself).
    private void DhWarmEnd() => _dhWarmStage = DhWarmDone;

    // Logout: the next character gets its own catalog warm-up (the framework re-fills the pre-built window per login).
    private void DhWarmRearm() { _dhWarmStage = 0; _dhWarmSince = -1; _dhWarmArtAt = 0; _dhWarmArtRun = 0; _dhWarmArtSince = -1; }
}
