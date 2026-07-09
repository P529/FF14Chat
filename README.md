# FF14Chat

A chat replacement plugin for Final Fantasy XIV (Dalamud). Tabbed chat with per-person tell tabs, command autocompletion, and persistent history.

See [PLAN.md](PLAN.md) for the implementation plan.

## Building

Requires .NET SDK and a Dalamud dev environment (XIVLauncher installed; Dalamud dev libs at `%AppData%\XIVLauncher\addon\Hooks\dev`).

```
dotnet build
```

## Installing (dev)

1. In game: `/xlsettings` → Experimental → Dev Plugin Locations
2. Add the path to `FF14Chat\bin\Debug\FF14Chat.dll`
3. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable FF14Chat
4. `/ff14chat` toggles the window

## Status

M0 — scaffold. Personal-use plugin, not for the official repo.
