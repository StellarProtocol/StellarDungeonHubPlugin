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

        InitDungeonConfirm();   // Dungeon Confirm: own window instead of the game's team_enter confirm (Plugin.DungeonConfirm*.cs)
        InitDungeonHub();       // Dungeon Hub: every dungeon / raid / activity in one window (Plugin.DungeonHub*.cs)
        _services.ClientState.Logout += OnSettingsLogout;

        // The one launcher tile — the Esc-menu gate icon, in-world only (the hub itself only renders in World).
        _launchers.Add(_services.Launcher.Register(new LauncherEntry(
            Title:   "Dungeon Hub",
            IconPng: CachedPng("dungeons-esc-icon.png"),
            IconKey: null,
            OnOpen:  DhOpen)
        { Group = LauncherGroup.Plugin,
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World }));

        _services.Log.Info("[DungeonHub] constructed");
    }

    // Logout runs synchronously (Update is world-gated): the settings child closes with the hub (house §5).
    private void OnSettingsLogout() => DhHideSettings();

    // Registers a hidden, draggable menu window (no launcher tile) — the Dungeon Confirm settings window.
    // DefaultRect is a fixed literal (calibrated to the 2560×1440 reference canvas), NOT derived from ScreenWidth:
    // "reset all HUD" restores it from a path where ScreenWidth reads 0 → a ScreenWidth-relative x lands off-screen
    // (WindowBuilder-Patterns.md §"DefaultRect must be a CONSTANT"). Every open re-centres it on the hub anyway.
    // In-world only and hidden during loading (house §5).
    private IWindowControl RegisterWindow(string id, string title, HudElement root, float width = 440f)
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          id,
                Title:       title,
                DefaultRect: new WindowRect(MathF.Min(2100f, 2540f - width), 20f, width, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World &&
                                   (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: root,
            OnClose: () => w!.SetVisible(false)));
        _windows.Add(w);
        return w;
    }

    public void Dispose()
    {
        _services.ClientState.Logout -= OnSettingsLogout;
        _dcSettingsCentre?.Stop();
        DisposeDungeonConfirm();
        DisposeDungeonHub();
        foreach (var l in _launchers) l.Dispose();
        foreach (var w in _windows) w.Remove();
    }
}
