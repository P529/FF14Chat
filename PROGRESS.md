# FF14Chat — Progress

A Dalamud chat-replacement plugin: tabbed chat, per-person tell tabs, command
autocompletion, persistent history. Personal use, sideloaded. `PLAN.md` holds
the original milestones and the v2 feature round; this file is the running
state of what actually works.

## Current feature set (as of 2026-07-11)

### Core
- Replaces vanilla chat (hides it while active; restores on close/cutscenes).
- Fixed tabs from config (General, Party, FC, System by default) + optional
  combined "All" tab; drag-reorder persists; mousewheel + custom arrow
  scrolling for overflowing tab strips.
- Auto-spawned tell tab per conversation partner (`Name@World`), closable,
  history backfilled, closed tabs stay closed until the partner chats again.
- SQLite history (30-day retention), hydrated on load with game timestamps;
  battle-log spam filtered at capture and purged from old databases.
- Sending via the game's chat box entry point; per-tab send channel
  (`/p` in Party, `/fc` in FC, `/tell` in tell tabs), 500-byte cap, sanitized.
- Enter and `/` open our input (keybind-level capture, occupied-state aware);
  Tab cycles tabs/channels (Shift+Tab backwards); input history; per-tab
  drafts; command autocomplete with descriptions from the TextCommand sheet.
- Active-channel indicator: input border + hint tinted with the destination
  channel color.
- FFXIV-native theming (4 themes), game font, item/map/player links with
  custom item tooltip cards.

### Added 2026-07-10/11
- **Tell presence dots** — colored dot on each tell tab: green online, red
  AFK, gray offline (friends only — the friend list is the only source that
  can prove offline), blue unknown (non-friend, not nearby, not in party).
  Sources: friend list info proxy (refreshed every 60 s, sticky per session so
  known friends never degrade to "unknown"), party list, nearby players,
  recent chat traffic (5-minute online window). Polls every 10 s, only for
  partners with open tabs.
- **Social-window "Send Tell" interception** — fixed after a full diagnosis
  (see PLAN.md "Bugfix gate" for the mechanism write-up: the social window
  pre-fills "/tell Name@World " via ChatLog addon event 0x31 rather than
  setting any tell state). Send Tell from any game UI now opens our tell tab
  with the input focused; vanilla chat input stays closed.
- **Unread tab glow** — pulsing gold outline alongside the unread badge.
- **Date separators** — dim "Tuesday, July 8" rule where the day changes.
- **Mention highlight** — gold wash + left bar on lines containing your full
  or first name (word-boundary, own messages skipped; toggle in settings).
- **Player context menu** — right-click a name in the log or a tell tab
  header: Send Tell, Target / Examine / Adventurer Plate (nearby only),
  Invite to Party, Copy Name.
- **Reopened tell tabs persist** — `OpenTellTab` now saves the config when it
  clears a partner's closed-state; previously reopening a tab via Send Tell /
  context menu didn't persist, so the tab vanished again on next load.
- **Send unfocuses input** (vanilla behavior) — Enter sends and hands control
  back to the game (WASD works immediately); the held Enter is eaten until
  released so vanilla chat doesn't pop open. Failed sends keep focus + draft.
- **Tab editor** — settings → "Tabs": add/rename/delete tabs, per-tab unread
  badge, catch-all, send command (normalized to one slash-word), channel
  checkbox grid (Chat / Linkshells / System groups, labels tinted with the
  channel color when enabled). Renames keep the saved tab-order slot and
  can't collide (empty/duplicate names revert); deleting needs Ctrl held and
  the last tab can't be deleted. The "Combine General and System" toggle
  disables itself when no tab with those names exists.
- **`/tell` name autocomplete** — typing `/tell ` (or `/t `) switches the
  suggestion popup to player names, sourced in priority order from open tell
  tabs, party, friend list, nearby players (max 10, source shown dim).
  Tab/click accepts as usual; the popup disappears once the typed text stops
  prefixing any known name (i.e. the message began). Friend names come from
  the same throttled info-proxy refresh the presence dots use, so the first
  popup after login may briefly miss friends.
- **Failed-tell errors land in the tell tab** — the game echoes an outgoing
  tell even when delivery fails (recipient in a duty, offline...), then emits
  an error line. The capture layer remembers the last outgoing echo for
  500 ms and stamps a following ErrorMessage/SystemError with that partner, so the
  failure shows up in the conversation it belongs to (still also routed to
  its normal channel tabs; persisted with the partner, so it hydrates back
  into the tab). Matched by kind + timing, not text — locale-independent.
  The echo's presence "online" note is retracted on failure.
- **Shareable build** — `dotnet build -c Release` produces
  `FF14Chat\bin\Release\FF14Chat\latest.zip` (~1.2 MB, Windows-only natives).
  Friend installs it as a dev plugin (extract → /xlsettings → Experimental →
  Dev Plugin Locations → enable in /xlplugins).

## Known gaps / accepted quirks
- Friend-list refresh runs every 60 s → presence dot can lag up to a minute
  after opening a tab or a friend logging off.
- Cross-world party invites from the context menu may fail (no content id
  available from chat payloads); Add Friend / Blacklist not implemented.
- 300 ms capture dedup can swallow a genuinely repeated identical message.
- Enter/`/` interception assumes default keybinds.
- ChatLog activation hook is signature-based — a game patch can kill it
  (plugin still loads; vanilla input focus just stops being suppressed).
- Older audit notes in PLAN.md still apply (CWLS shared colors, stale
  config ids, no native item tooltip).

## Next up
- v2 feature round complete (tab editor + /tell autocomplete were the last
  two items). Pending in-game verification of both.
- Deferred: history search (data layer ready). Rejected: presence tooltip.

## State of the tree
- All of the above is **uncommitted** on `master` (last commit `dc15201`).
- Version bumped to 0.1.0.0 in the csproj alongside the Release packaging
  changes (win-x64 RID, stable Debug output path for the dev plugin).
