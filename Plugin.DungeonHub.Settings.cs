using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Hub header gear → the Dungeon Confirm settings window (user-approved mockup 2026-11-07: icon-only gear immediately
// left of the ✕). The settings window has no launcher tile of its own; this is its only way in.
//
// Open = house OpenCentred (WindowCenterer.cs): show, ALWAYS BringToFront, re-centre on the hub's current rect. Always
// front because ZFront is sticky (WindowBuilder-Patterns.md §"ZFront is STICKY", CLAUDE.md Rule 5) and this plugin
// already fronts the hub and the confirm windows on every open — a never-fronted settings window would land UNDER the
// hub it was opened from.
public sealed partial class Plugin
{
    private WindowCenterer? _dcSettingsCentre;

    private void DhOpenSettings()
    {
        // Lazy: CentreOf captures the hub control, which only exists after InitDungeonHub.
        _dcSettingsCentre ??= new WindowCenterer(_services.Framework, _dcSettingsWindow, WindowCenterer.CentreOf(_dhWin));
        _dcSettingsCentre.OpenCentred();
    }

    private void DhHideSettings()
    {
        _dcSettingsCentre?.Stop();
        _dcSettingsWindow.SetVisible(false);
    }

    // Icon-only gear (house §4, same 28 px build as DhCloseButton): no fill at rest + muted glyph; hover = accent wash +
    // accent glyph (the ✕ uses red — the gear isn't destructive). Generated PNG like the ✕ (DcGlyph.Gear, .Art.cs),
    // re-baked per colour by DcGlyphPng's cache.
    private HudElement DhSettingsButton()
    {
        var hover = new DcHover();
        float glyph = DcSE(12f), side = (DhCloseW - glyph) / 2f;
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(side, DhCloseW),
                new ImageElement(() => DcGlyphPng(DcGlyph.Gear, glyph, hover.On ? Menu.MenuAccent : MutedColor()), (int)glyph, (int)glyph),
                new SpacerElement(side, DhCloseW),
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: DhOpenSettings) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fade(hover.On ? Over(Menu.MenuAccent with { A = 0.22f }, Menu.MenuBackground) : Transparent),
                CornerRadius = DhCloseW / 2f - 1f, PaddingX = 0f, PaddingY = 0f, MaxWidth = DhCloseW,
            }, Width: DhCloseW);
    }
}
