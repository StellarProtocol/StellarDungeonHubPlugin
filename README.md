# Stellar.DungeonHub

A StellarResonance plugin that puts every dungeon in one place:

- **Dungeon Hub**: one window for every dungeon, raid, World Boss, Guild Hunt and activity. Pick a dungeon and a
  difficulty, then enter Solo, start a Match, or go in with your Party. Open it from the launcher tile, the
  **Dungeon Hub** button on the game's Esc menu, or a hotkey you assign.
- **Dungeon Confirm**: a smaller window in place of the game's full-screen "confirm dungeon entry" screen
  (5-player and raid, team entry and match found), with its own open sound and countdown ticks. On by default; turn it
  off (and back on) with the gear button in the Dungeon Hub header.

## Build

```bash
dotnet build -c Release
```

Requires Stellar framework 2.30.0 or newer (window pre-building, plus the round slider knobs and window-drag fixes from 2.29.1). For a local deploy, copy `Local.props.example` to `Local.props` and set
`GameInstallDir` (deploys to `<game>\stellar\plugins\dungeonhub\`).

## License

AGPL-3.0-or-later.
