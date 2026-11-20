using Stellar.Abstractions.Domain;

namespace Stellar.DungeonHub;

// Dungeon Hub — the game's free cursor while the hub is up (user-approved 2026-11-21). Opening the hub from its hotkey
// in camera mode used to leave the cursor locked until the player Alt-clicked; now the hub asks for the cursor the way
// the game's own panels (chat, the badge) do: Z.UIMgr:AddShowMouseView(key) / RemoveShowMouseView(key)
// (Knowledge Base\Harmony-Rewired-Input-Patching.md §"Game free-cursor"). That keyed set coexists with the game's
// views, so closing a bag over the hub doesn't drop our request — and never set the ZMouseManager UI source directly,
// UIManager.UpdateMouseVisible rewrites it on every view change.
//
// Safe to repeat (release_4.7 ui_manager.lua:649-667): Add is `showMouseView_[key] = true` + UpdateMouseVisible (a
// set, so a duplicate Add is a no-op), Remove is `showMouseView_[key] = nil` + UpdateMouseVisible (a missing key is
// harmless). So DhOpen re-Adds on EVERY open whatever _dhMouseOn says — a relog's new Z.UIMgr can't be left without it.
//
// Edge-triggered: DhOpen / DhHide call it at once; the 2 Hz DhTick only compares a bool and acts on a change, which
// covers what doesn't pass through DhHide — the loading screen (ShouldRender hides the hub, _dhShow stays true; the
// cursor is released and taken again once the new scene is up) and leaving the world. Main hub window only: the
// Dungeon Confirm popups and the settings popover don't touch it.
public sealed partial class Plugin
{
    private bool _dhMouseOn;   // our key is (believed to be) in Z.UIMgr.showMouseView_

    private const string DhMouseAddLua =
        "pcall(function() local m = Z.UIMgr if m then m:AddShowMouseView('stellar_dungeonhub') end end)";
    private const string DhMouseRemoveLua =
        "pcall(function() local m = Z.UIMgr if m then m:RemoveShowMouseView('stellar_dungeonhub') end end)";

    // Same condition as the window's ShouldRender (.Window.cs): the cursor follows what the player actually sees.
    private bool DhMouseWanted => _dhShow && _services.ClientState.Phase == GamePhase.World &&
                                  (_services.ClientState.UiState & GameUIState.Loading) == 0;

    // force: Add even when we think it's already in (every open — see the header).
    private void DhSyncMouse(bool force = false)
    {
        bool want = DhMouseWanted;
        if (want == _dhMouseOn && !(want && force)) return;
        if (!want)
        {
            // Released either way: with Lua down (title / relog) the old Z.UIMgr and its set are gone with it, and a
            // stale "on" would only make the next open skip its Add.
            _dhMouseOn = false;
            if (_services.Lua.Ready) _services.Lua.DoString(DhMouseRemoveLua);
            return;
        }
        if (!_services.Lua.Ready) return;   // not ready yet: stays off, the next tick tries again
        _services.Lua.DoString(DhMouseAddLua);
        _dhMouseOn = true;
    }

    // Plugin unload: never leave the game's cursor stuck free.
    private void DhReleaseMouse()
    {
        if (!_dhMouseOn) return;
        _dhMouseOn = false;
        if (_services.Lua.Ready) _services.Lua.DoString(DhMouseRemoveLua);
    }
}
