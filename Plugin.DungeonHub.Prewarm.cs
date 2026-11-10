using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeons hub — warm-up after entering the world, so the FIRST open doesn't freeze the game (user report 2026-11-08;
// later opens were always fine). What a first open paid all at once, and what is now done ahead:
//   1. CATALOG — the first FULL poll walks every dungeon / raid / activity config table (GetDatas, GetRow) and the
//      game loads a table on its first touch. Run one FULL poll hidden → the open's own FULL poll finds them loaded.
//      Its entry names are handed to IWindowHost.PrewarmText so their glyphs rasterize a few per frame in advance.
//   2. GLYPHS — the header's ✕ and ⚙ PNGs (DcGlyphPng, both colours) are baked now instead of inside the build.
//   3. ART — the saved tab's list thumbs + the selected entry's band are requested through DhLoad's per-frame budget
//      (.ArtLoad.cs) — async loads, so they're usually in by the first open. Raid composes are NOT warmed (the RTs are
//      released on every hide anyway; DhComposeAllowed spreads them).
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
    private const int DhWarmArtPerTick = 3;

    private int    _dhWarmStage;        // 0 waiting · 1 catalog · 2 glyphs · 3 art · 4 done
    private double _dhWarmSince = -1;   // DcNow the world first looked settled (-1 = not yet / reset by loading)
    private int    _dhWarmArtAt;        // next list slot to ask art for (stage 3)

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
                    foreach (var e in _dhLists[t]) { _services.Windows.PrewarmText(e.Name); _services.Windows.PrewarmText(e.SubText); }
                break;
            case 2:   // glyphs: the header ✕ (muted / hover red) and ⚙ (muted / hover accent), as Window.cs / Settings.cs bake them
            {
                float glyph = DcSE(12f);
                DcGlyphPng(DcGlyph.Cross, glyph, MutedColor()); DcGlyphPng(DcGlyph.Cross, glyph, DhRed);
                DcGlyphPng(DcGlyph.Gear, glyph, MutedColor()); DcGlyphPng(DcGlyph.Gear, glyph, Menu.MenuAccent);
                break;
            }
            case 3:   // art: a few list thumbs (+ the band) per tick through the load budget — REQUESTS only: a raid entry
            {         // asks its two paths but never composes here (an RT made while hidden would be held until a hide)
                var list = DhList;
                int n = System.Math.Min(DhListSlots, list.Count), asked = 0;
                if (_dhWarmArtAt == 0 && DhCur is { ArtBg.Length: 0 }) { DhBandTex(); asked++; }
                while (asked < DhWarmArtPerTick && _dhWarmArtAt < n)
                {
                    var e = list[_dhWarmArtAt];
                    if (e.ArtBg.Length > 0) { DhLoad(DhArtOf(e), out _); DhLoad(e.ArtBg, out _); _dhWarmArtAt++; }
                    else DhThumbTex(_dhWarmArtAt++);
                    asked++;
                }
                if (_dhWarmArtAt < n) return;   // more next tick
                break;
            }
        }
        if (++_dhWarmStage >= DhWarmDone) DhWarmEnd();
    }

    // Done, or a real open came first (it does the rest itself).
    private void DhWarmEnd() => _dhWarmStage = DhWarmDone;

    // Logout: the next character gets its own catalog warm-up (the framework re-fills the pre-built window per login).
    private void DhWarmRearm() { _dhWarmStage = 0; _dhWarmSince = -1; _dhWarmArtAt = 0; }
}
