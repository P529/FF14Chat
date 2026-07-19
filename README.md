# FF14Chat

[![Build](https://github.com/P529/FF14Chat/actions/workflows/build.yml/badge.svg)](https://github.com/P529/FF14Chat/actions/workflows/build.yml)

A chat replacement for Final Fantasy XIV, built as a [Dalamud](https://github.com/goatcorp/Dalamud) plugin. It hides the vanilla chat log and replaces it with a tabbed, themed, persistent chat window. Toggle with `/ff14chat`.

## Features

- **Tabs** — configurable fixed tabs (General, Party, FC, System by default) with drag-reorder, unread badges with a pulsing glow, an optional combined "All" tab, and a full tab editor in settings (add/rename/delete tabs, per-tab channel filters, unread tracking, catch-all, send channel).
- **Tell tabs** — a separate tab per conversation partner (`Name@World`), auto-spawned on the first tell, closable, restored across restarts with history backfilled. Presence dot per partner: green online, red AFK, gray offline (friends), blue unknown.
- **Send Tell integration** — "Send Tell" from any game menu (social window, friend list, search) opens the right tab with the input focused; vanilla chat stays closed.
- **Per-tab send channel** — plain text in the Party tab goes out as `/p`, in FC as `/fc`, in a tell tab as `/tell Name@World`; typed `/commands` always pass through unchanged.
- **Input, like vanilla but better** — Enter or `/` opens the input (occupied states like cutscenes and NPC dialogue are respected); sending hands control straight back to the game; Tab cycles tabs/channels (Shift+Tab backwards); up/down recalls sent history; drafts are kept per tab.
- **Autocomplete** — every game command and emote with descriptions from the game's own data, plugin commands included; `/tell ` completes player names from open tells, party, friends, and nearby players.
- **Persistent history** — SQLite-backed, 30 days of retention, restored on login with correct game timestamps. Tell conversations survive restarts.
- **Rendering** — per-channel colors, clickable item/map/player links, item tooltip cards, mention highlighting, date separators, game font, four FFXIV-native themes (Muted Gold, Rich Gold, Classic Blue, FF7 Remake) with an opacity slider.
- **Emotes** — Discord-style shortcodes (`:sob:`, `:joy:`, ~1900 names) render inline as [Twemoji](https://github.com/jdecked/twemoji) images; typing `:` plus two letters autocompletes. Fully bundled, nothing downloaded; others just see the plain text.
- **Player context menu** — right-click a name in the log or a tell tab: Send Tell, Target, Examine, Adventurer Plate, Invite to Party, Copy Name.

## Installing

### Via the in-game plugin installer (recommended)

Gets you automatic updates whenever a new release is published.

1. In game, type `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add: `https://github.com/P529/FF14Chat/releases/latest/download/repo.json`
3. Save, then `/xlplugins` → search "FF14Chat" → install
4. `/ff14chat` toggles the window

### Manual (dev plugin)

1. Download `latest.zip` from the [latest release](https://github.com/P529/FF14Chat/releases/latest) and extract it somewhere permanent
2. `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the folder
3. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable FF14Chat

## Usage notes

| Input | Action |
|---|---|
| `/ff14chat` | Toggle the window |
| Enter | Open the chat input (sends and releases it when typing) |
| `/` | Open the input with a `/` pre-typed |
| Tab (empty input) | Cycle tabs, or the game's active channel on General/System |
| Shift+Tab | Same, backwards |
| Tab (while typing `/...`) | Accept the highlighted suggestion |
| ↑ / ↓ | Sent-message history, or move the suggestion highlight |
| Right-click a player name | Context menu (tell, target, examine, invite...) |
| Right-click a tab | Close / mark read (tell tabs) |

Settings (`/xlplugins` → FF14Chat → gear icon, or the gear in the window): themes, font size, opacity, tab editor, mention highlight, presence dots, vanilla chat hiding, window lock.

Chat history is stored in `%AppData%\XIVLauncher\pluginConfigs\FF14Chat\chat.db` (SQLite). Delete the file to wipe history; it prunes itself to 30 days.

## Updating after a game or Dalamud patch

Plugins break on patch days; this one is built to fail soft (a broken hook logs and disables itself rather than crashing). The playbook, in order of likelihood:

1. **Wait for Dalamud.** After a game patch, Dalamud itself needs updating before any plugin loads. Nothing to do on our side until XIVLauncher lets you into the game with plugins enabled.
2. **Usually: nothing.** If Dalamud's API level didn't change, the plugin keeps loading and working. Done.
3. **API level bump** (Dalamud refuses to load the plugin, "outdated API level"): bump the SDK version in `FF14Chat/FF14Chat.csproj` — the first line, `<Project Sdk="Dalamud.NET.Sdk/15.0.0">` — to the newest [Dalamud.NET.Sdk on NuGet](https://www.nuget.org/packages/Dalamud.NET.Sdk), then `dotnet build`. Fix whatever no longer compiles (typically renames in Dalamud or FFXIVClientStructs — the compiler errors point the way), reload, done.
4. **Silent breakage** (loads fine, but sending chat or Send Tell interception stopped working): these ride on FFXIVClientStructs and a signature hook. Rebuild against the newest SDK as in step 3; check `%AppData%\XIVLauncher\dalamud.log` for the hook failure line.
5. **Publish the fix:** bump `<Version>` in the csproj, commit, tag, push the tag (see below). Everyone on the custom repo gets the update in game.

The **weekly CI build** compiles the plugin against the newest Dalamud every Monday — a failed run in the Actions tab is the early warning that step 3 or 4 is coming. You can also trigger it manually from the Actions tab on patch day.

## Releasing

Releases are cut by tag:

```
# 1. bump <Version> in FF14Chat/FF14Chat.csproj (e.g. 0.2.0.0), commit
# 2. tag and push — the tag must match the version:
git tag v0.2.0
git push origin v0.2.0
```

The release workflow builds `latest.zip`, generates `repo.json`, and publishes both as a GitHub release with generated notes. The custom-repo URL always points at the newest release, so in-game updates need nothing further. A tag that doesn't match the csproj version fails the workflow on purpose.

## Building from source

Requirements: [.NET SDK 10](https://dotnet.microsoft.com/download), and Dalamud's dev assemblies — either a normal XIVLauncher install (they live in `%AppData%\XIVLauncher\addon\Hooks\dev`) or any copy of [dalamud-distrib latest.zip](https://goatcorp.github.io/dalamud-distrib/latest.zip) with `DALAMUD_HOME` pointing at it.

```
dotnet build FF14Chat/FF14Chat.csproj              # Debug: for dev-plugin loading
dotnet build FF14Chat/FF14Chat.csproj -c Release   # produces bin/Release/FF14Chat/latest.zip
```

For development, register `FF14Chat\bin\Debug\` as a Dev Plugin Location and rebuild + reload from `/xlplugins`.

### Layout

| | |
|---|---|
| `Plugin.cs` | Entry point, service wiring, config migrations |
| `Services/ChatCapture.cs` | `IChatGui` subscription → message model, dedup, battle-spam filter |
| `Services/TabManager.cs` | Routes messages into fixed and tell tabs |
| `Services/MessageDatabase.cs` | SQLite persistence, single background writer |
| `Services/ChatSender.cs` | Sending via the game's chat box entry point, sanitation |
| `Services/PresenceTracker.cs` | Online/AFK/offline for tell partners |
| `Services/CommandIndex.cs` | Autocomplete sources |
| `Services/Emotes.cs` | `:shortcode:` emotes: bundled Twemoji textures + completion |
| `Ui/MainWindow.cs` | The chat window: tabs, log, input, links, hooks |
| `Ui/SettingsWindow.cs` | Settings and the tab editor |

`PLAN.md` holds the original milestone plan and design decisions; `PROGRESS.md` is the running state of what works and what's deferred.

## Known limitations

- Enter/`/` interception assumes default chat keybinds.
- Presence dots degrade to "unknown" inside instanced content (the friend list can't be read there) and can lag up to a minute behind.
- Cross-world party invites from the context menu may fail (no content id available from chat payloads).
- IME input (Japanese/Chinese) inside ImGui text fields is rough — a known ImGui limitation.
- A game patch can break sending or the Send Tell interception until a rebuild (see the update playbook above).

## Credits

- Emoji artwork: [Twemoji](https://github.com/jdecked/twemoji) v15.1.0, copyright Twitter, Inc and other contributors — graphics licensed [CC-BY 4.0](https://creativecommons.org/licenses/by/4.0/), bundled unmodified as 72×72 PNGs in `FF14Chat/Data/emotes.zip`.
- Emote shortcode names: the alias list from GitHub's [gemoji](https://github.com/github/gemoji) (MIT), bundled as `FF14Chat/Data/emotes.json`.

Both source projects are pinned as submodules under `third_party/` at the exact versions the bundled data was generated from (regenerate: map gemoji `db/emoji.json` aliases → Twemoji `assets/72x72/` codepoint filenames → zip). The submodules are not needed to build — the generated data is committed.

## Disclaimers

- All Dalamud plugins are technically against the FFXIV Terms of Service. This one is quiet and personal-use; use at your own risk and keep plugin talk out of the game.
- Built from scratch. [ChatTwo](https://github.com/Blooym/ChatTwo) (EUPL-1.2) was studied as a reference for hook signatures and pitfalls; no code was copied.
- FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This project is not affiliated with Square Enix.
