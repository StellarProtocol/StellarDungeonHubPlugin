using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Hub — moved out of StellarExperimentPlugin (2026-11-07) as its own plugin:
//   • Dungeon Hub (Plugin.DungeonHub*.cs): one window to reach every dungeon / raid / activity. Opened from the launcher
//     tile, the game's Esc-menu rail button and an unbound hotkey.
//   • Dungeon Confirm (Plugin.DungeonConfirm*.cs): a house-style window replacing the game's team_enter confirm. Its
//     settings window opens from the hub header's gear (Plugin.DungeonHub.Settings.cs).
// Knowledge Base: Dungeon-Access-Hub.md, Dungeon-Confirm-Replacement.md.
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Dungeon Hub";

    private readonly IPluginServices _services;
    private readonly IConfigSection  _cfg;
    private readonly List<IWindowControl> _windows   = new();
    private readonly List<IDisposable>    _launchers = new();

    public Plugin(IPluginServices services)
    {
        _services = services;
        _cfg = _services.Config.GetSection("settings");
        LoadWindowOpacity();    // before any window registers: their FrameOpacity / fills read it (Plugin.WindowOpacity.cs)

        InitDungeonConfirm();   // Dungeon Confirm: own window instead of the game's team_enter confirm (Plugin.DungeonConfirm*.cs)
        InitDungeonHub();       // Dungeon Hub: every dungeon / raid / activity in one window (Plugin.DungeonHub*.cs)
        _services.ClientState.Logout += OnSettingsLogout;

        // The one launcher tile — the gate icon in WHITE (the hub header's recolour, .DungeonHub.Icon.cs), in-world only
        // (the hub itself only renders in World). The grey original looked dark / muddy next to the other launchers'
        // white icons (user, 2026-11-07); the Esc-menu rail button keeps the grey one, which matches the game's rail.
        // Baked once here (bytes, not a Func); a failed recolour falls back to the grey PNG.
        _launchers.Add(_services.Launcher.Register(new LauncherEntry(
            Title:   "Dungeon Hub",
            IconPng: DhGateWhitePng() ?? CachedPng("dungeons-esc-icon.png"),
            IconKey: null,
            OnOpen:  DhOpen)
        { Group = LauncherGroup.Plugin,
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World }));

        _services.Log.Info("[DungeonHub] constructed");
    }

    // Logout runs synchronously (Update is world-gated): the settings child closes with the hub (house §5).
    private void OnSettingsLogout() => DhHideSettings();

    public void Dispose()
    {
        _services.ClientState.Logout -= OnSettingsLogout;
        _dcSettingsCentre?.Stop();
        FlushOpacity();
        DisposeDungeonConfirm();
        DisposeDungeonHub();
        foreach (var l in _launchers) l.Dispose();
        foreach (var w in _windows) w.Remove();
    }
}
