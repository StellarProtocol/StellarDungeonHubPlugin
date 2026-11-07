using System;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// "Custom window opacity" (settings → WINDOW, off by default; ChatTools Plugin.WindowOpacity.cs pattern). Off, every
// Dungeon Hub window follows the framework's Themes → Window Opacity (IChromeStyle.WindowOpacity) like any window; on,
// the slider value (30–100 %) is used instead for ALL of them — the hub, both Dungeon Confirm windows and the settings.
//   • The frame: WindowSpec.FrameOpacity = DhFrameOpacity on each registration (null = follow the framework). The
//     framework polls it live, no rebuild.
//   • Our fills: Style.cs WindowOpacity() reads the SAME effective value, so every bubble fill fades with the frame.
// Persistence: the toggle saves at once; the slider applies live while dragging (the Funcs read the field) but the
// config write is debounced (one write after the drag settles, from DcTick) and flushed when the settings close.
public sealed partial class Plugin
{
    private const string CfgOpacityOn = "window_opacity_custom", CfgOpacity = "window_opacity";
    private const float OpacityMin = 0.3f, OpacityMax = 1f;   // the framework's own WindowOpacity clamp range
    private const float OpacityDefault = 0.85f, OpacitySaveDelaySec = 0.5f;

    private bool  _customOpacityOn;
    private float _customOpacity = OpacityDefault;
    private double _opacitySaveAt = -1;   // TimeNow the pending slider value is due to save (-1 = nothing pending)

    private void LoadWindowOpacity()
    {
        _customOpacityOn = _cfg.Get<bool>(CfgOpacityOn, false);
        _customOpacity = Math.Clamp(_cfg.Get<float>(CfgOpacity, OpacityDefault), OpacityMin, OpacityMax);
    }

    // WindowSpec.FrameOpacity of every Dungeon Hub window: null = follow the framework.
    private float? DhFrameOpacity() => _customOpacityOn ? _customOpacity : null;

    private void SetCustomOpacityOn(bool on)
    {
        _customOpacityOn = on;
        _cfg.Set<bool>(CfgOpacityOn, on);
        _cfg.Save();
    }

    private void SetCustomOpacityPct(float pct)
    {
        _customOpacity = Math.Clamp(MathF.Round(pct) / 100f, OpacityMin, OpacityMax);   // live: next poll picks it up
        _opacitySaveAt = _services.Framework.TimeNow + OpacitySaveDelaySec;
    }

    // From DcTick (200 ms, runs whatever the confirm state).
    private void TickOpacitySave()
    {
        if (_opacitySaveAt >= 0 && _services.Framework.TimeNow >= _opacitySaveAt) SaveOpacity();
    }

    private void FlushOpacity() { if (_opacitySaveAt >= 0) SaveOpacity(); }

    private void SaveOpacity()
    {
        _opacitySaveAt = -1;
        _cfg.Set<float>(CfgOpacity, _customOpacity);
        _cfg.Save();
    }

    // Settings → WINDOW panel: the toggle row, and the slider row only while it is on.
    private HudElement DsWindowPanel() => DcPanel(() => "WINDOW", new SpacerElement(0f, 0f),
        DsToggleRow("Custom window opacity", "Off = follow the theme's Window Opacity",
            () => _customOpacityOn, () => SetCustomOpacityOn(!_customOpacityOn)),
        new ConditionalElement(() => _customOpacityOn, new RowElement(new HudElement[]
        {
            new CellElement(DcSmall(() => "Opacity", MutedColor, DcFsBody), Width: DsVolLabelW),
            new CellElement(new SliderElement(() => MathF.Round(_customOpacity * 100f), SetCustomOpacityPct,
                OpacityMin * 100f, OpacityMax * 100f) { SquareHandle = true, HandleSize = DcSE(14f) }, Weight: 1f),
            new CellElement(DcBold(() => $"{(int)MathF.Round(_customOpacity * 100f)}%", TextColor, DcFsSmall, DsVolValW,
                TextAlign.Right), Width: DsVolValW),
        }, Gap: DsGap)));
}
