# FF14 Chat Replacement Plugin — Implementation Plan

## Goal

A Dalamud plugin providing a replacement chat window with:

- **Tabs**, including an auto-spawned tab per tell conversation (keyed by `Name@World`)
- **Command autocompletion** in the input field
- **History persistence** across relogs and restarts

Scope decisions (locked in):

- Built **from scratch**, using [ChatTwo](https://github.com/Blooym/ChatTwo) source as a study reference for hooks and pitfalls (do not copy code — it's EUPL-1.2).
- **Personal use** only, sideloaded as a dev plugin. No official-repo submission rules apply.
- Vanilla chat stays visible in v1. Hiding it + Enter-key capture is a v2 item.

## Tech stack

| Piece | Choice |
|---|---|
| Framework | Dalamud plugin, C# / .NET (latest Dalamud target, currently .NET 9) |
| Project SDK | `Dalamud.NET.Sdk` (start from goatcorp/SamplePlugin template) |
| UI | ImGui via Dalamud `WindowSystem` |
| Game data | Lumina Excel sheets via `IDataManager` (TextCommand, Emote, UIColor, World) |
| Memory/native | `FFXIVClientStructs` (already bundled with Dalamud) for sending chat |
| Persistence | SQLite via `Microsoft.Data.Sqlite` (ChatTwo migrated LiteDB → SQLite for good reasons; skip that detour) |
| Testing | Dev plugin load through XIVLauncher dev menu; unit tests only for pure logic (parsing, tab routing) |

## Architecture

```
FF14Chat/
├── Plugin.cs                  # entry point, service wiring
├── Services/
│   ├── MessageStore.cs        # in-memory ring buffer + SQLite persistence
│   ├── ChatCapture.cs         # IChatGui.ChatMessage subscription → Message model
│   ├── ChatSender.cs          # UIModule.ProcessChatBox wrapper, input sanitation
│   └── CommandIndex.cs        # autocomplete source (TextCommand sheet + ICommandManager)
├── Model/
│   ├── Message.cs             # timestamp, XivChatType, sender, world, SeString payloads, plaintext
│   ├── Tab.cs                 # name, channel filter set, unread count
│   └── TellTab.cs             # Tab specialization keyed by Name@World
├── Ui/
│   ├── MainWindow.cs          # tab bar + log region + input row
│   ├── LogRenderer.cs         # SeString → ImGui (colors, wrapping, links), ImGuiListClipper
│   ├── InputField.cs          # InputText callbacks, autocomplete popup, history recall
│   └── SettingsWindow.cs      # tab config, retention, appearance
└── Configuration.cs           # IPluginConfiguration: tab definitions, options
```

Data flow: `IChatGui.ChatMessage` → `ChatCapture` normalizes into `Message` → `MessageStore` appends (memory + async SQLite write) → each `Tab` filters by `XivChatType` (tell tabs also match sender/target identity) → `LogRenderer` draws visible slice.

## Milestones

### M0 — Scaffold (half a day)
- Clone SamplePlugin template, rename, verify it loads via XIVLauncher dev plugins.
- Git init, .gitignore, basic README.
- **Done when:** `/ff14chat` command opens an empty ImGui window in game.

### M1 — Capture + basic log window
- Subscribe `IChatGui.ChatMessage`; build `Message` model (keep original `SeString` payloads AND extracted plaintext).
- Ring buffer (e.g. 10k messages) in `MessageStore`.
- Single "General" tab rendering plaintext with per-channel color from the `UIColor` / `LogColor` mapping; auto-scroll with "pinned to bottom" detection; `ImGuiListClipper` for perf from day one.
- **Done when:** window mirrors live chat with correct colors and doesn't hitch at high message rates.

### M2 — Tabs engine + tell tabs
- Config-driven tabs: name + set of `XivChatType`s (presets: General, Battle, System).
- Unread badge per tab; switch clears it.
- **Tell tabs:** on `TellIncoming`/`TellOutgoing`, resolve `Name@World` from sender payloads (PlayerPayload carries world), spawn/reuse a closable tab per person. Tells also still land in any general tab that includes the tell channels.
- Right-click tab context menu: close, pin, mark read.
- **Done when:** receiving a tell from a new person pops a new tab; conversation history for that person accumulates there.

### M3 — Input + sending
- Input row: channel selector (current channel indicator like vanilla) + `InputText`.
- Sending via `UIModule.ProcessChatBox` through FFXIVClientStructs — sanitize (strip newlines, length ≤ 500 bytes, reject control chars) since this bypasses the game's own input validation. Study ChatTwo's `ChatBox`/sanitizer here carefully; this is the one place a bug can send garbage to the server.
- In a tell tab, plain text auto-sends as `/tell Name@World <text>`; typed `/commands` pass through unchanged.
- Input history (up/down arrows), per-tab draft preservation.
- **Done when:** can hold a full tell conversation from its tab without touching vanilla chat.

### M4 — Command autocomplete
- `CommandIndex` sources:
  - Game commands: `TextCommand` Excel sheet (`Command`, `ShortCommand`, `Alias`, `ShortAlias`, `Description`) — gives every native command with localized descriptions for free.
  - Plugin commands: `ICommandManager.Commands`.
  - Emotes: `Emote` sheet `TextCommand` references.
- Trigger: input starts with `/` and caret is in the first word. Popup above input showing matches (prefix match first, then substring), description in dim text.
- Navigation: Tab/arrow keys cycle, Enter/Tab accepts — implemented via `ImGuiInputTextFlags.CallbackAlways | CallbackHistory | CallbackCompletion` (arrow keys inside InputText require the history callback; this is fiddly, budget time).
- **Done when:** typing `/pe` offers `/petition`, `/pet` etc. with descriptions, Tab completes.

### M5 — History persistence
- SQLite schema: `messages(id, ts, type, sender, sender_world, content BLOB /* SeString.Encode() */, plaintext TEXT)`; index on `(type, ts)` and `(sender, sender_world, ts)`.
- Async single-writer queue (never block the render/chat thread on disk).
- On plugin load: hydrate last N (configurable, default ~1000) messages per tab; tell tabs restore for people with recent history.
- Retention setting (default: keep 30 days, vacuum on startup).
- **Done when:** restart the game, tell tabs and history reappear.

### M6 — SeString rendering fidelity
- Render payload-by-payload: `UIForeground`/`UIGlow` colors, italics, game icon glyphs (Dalamud font covers the private-use-area icons).
- Clickable links: item links (open item tooltip / ChatGui link handling), map links (flag + open map), player names (context menu: reply, invite, add friend via native agents where feasible — otherwise start with "copy name" + "reply").
- Auto-translate payload rendering (the ⟨⟩ brackets).
- **Done when:** a linked item in chat is visually distinct and clickable.

### M6.5 — FFXIV-native theming (user requirement, before "final")
- The window must look like a Final Fantasy XIV window, not default ImGui: dark translucent panel, FFXIV-style borders/corners, matching fonts and spacing.
- Approach: dedicated ImGui style push (colors, rounding, borders) around the whole window; optionally draw the game's own window frame textures (ULD assets via ITextureProvider) behind the content like some plugins do.
- Titlebar-less "locked" chat look with a subtle resize grip, matching vanilla chat's feel.

### M7 — v2 / polish backlog (unordered)
- Hide vanilla chat log (`AddonChatLog` visibility) + capture Enter to focus this window.
- Player-name autocomplete (party, FC, recent tells, nearby objects).
- Search across history (SQLite `plaintext` LIKE or FTS5).
- Per-tab timestamp format, font size, opacity; window docking/lock.
- Tab flash/sound on tell.
- Screenshot-hide mode.

## Risks & gotchas (mostly learned from ChatTwo's issue tracker)

1. **Patch breakage.** `IChatGui` capture is a stable Dalamud API; sending via ClientStructs breaks on patches until ClientStructs updates. Acceptable for personal use — expect a few broken days after each patch.
2. **IME input (JP/CN) inside ImGui InputText is notoriously bad.** If you ever type Japanese, this becomes the hard problem; ChatTwo has years of issues about it. Out of scope unless needed.
3. **Retainer/system messages ordering** — `ChatMessage` fires on the game's thread; marshal to your store thread-safely (Dalamud framework thread vs render thread).
4. **SeString round-tripping** — always keep the original encoded bytes; regenerating from plaintext loses links and colors.
5. **ToS** — all plugins are technically against ToS; personal use, standard don't-advertise-in-game hygiene.

## Suggested order of attack

M0 → M1 → M2 gets the "chat with tell tabs" core visible fast (~the first week of evenings). M3 makes it usable, M4 delivers the autocomplete ask, M5 persistence. M6 fidelity is where polish time disappears — timebox it.
