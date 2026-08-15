# FF14Chat — Progress

A Dalamud chat-replacement plugin: tabbed chat, per-person tell tabs, command
autocompletion, persistent history. Personal use, sideloaded. `PLAN.md` holds
the original milestones and the v2 feature round; this file is the running
state of what actually works.

## Current feature set (as of 2026-07-12)

### Core
- Replaces vanilla chat (hides it while active; restores on close/cutscenes).
- Fixed tabs from config (General, Party, FC, System by default) + optional
  combined "All" tab; drag-reorder persists; mousewheel + custom arrow
  scrolling for overflowing tab strips.
- Auto-spawned tell tab per conversation partner (`Name@World`), closable,
  history backfilled, closed tabs stay closed until the partner chats again.
- SQLite history (configurable retention: session-only → forever, default
  30 days; DB size shown in settings), hydrated on load with game timestamps;
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

### Added 2026-08-16 (v0.3.4)
- **"Try On Original" on examined gear.** The examine window is not an
  inventory: `AgentInspect` holds the examined pieces itself (`Items` =the real
  gear, `GlamourItems` = what they are wearing) and opens a plain
  `AgentContext` menu, so none of the `AgentInventoryContext` path applied to
  it — the entry simply never appeared there. Resolved off
  `AgentInspect->SelectedItemSlot` instead, with no glamour test: examine's own
  Try On previews the glamoured *and dyed* appearance either way, so there is
  always something to strip.
- **Ctrl+right-click tries an item on**, no menu at all (opt-in). Dalamud's
  `OnMenuOpened` fires once the menu is committed, so suppression has to happen
  a step earlier: hooks on `AgentInventoryContext.OpenForItemSlot` (bags,
  armoury, character sheet, retainers) and `AgentContext.OpenContextMenuForAddon`
  for the examine window, the latter gated on the owning addon id so only that
  window's menus can be swallowed. Chat item links take the same shortcut in
  ImGui. Both hooks are non-fatal: a patch-day break costs the shortcut, not
  the menu entry.
- **Tweaks tab in settings** — the game-side extras in one place: `/examine`
  and `/mountid` (live toggles that actually add/remove the command handler, so
  switching one off releases the name), the "Try On Original" entry and the
  Ctrl+right-click shortcut. Command autocomplete follows automatically; it
  scans the registered commands live.
- Timestamps render with a trailing space. The line continues with
  `SameLine(0, 0)`, so the gap has to be part of the stamp's own item width.

### Added 2026-08-07 (v0.3.3)
- **`/mountid [Name@World]`** — prints what a character is riding, as a clickable
  item link to the whistle/card that teaches the mount, so the tooltip and the
  item context menu come along for free. No argument reads the current target
  (any `ICharacter`, so mounted NPCs work too); a name is resolved out of the
  object table like `/examine`, since the mount can only be read off an object
  that is actually loaded. Mounts with no teaching item — the Company Chocobo
  above all, plus quest and event rewards granted server-side — fall back to
  the `Mount` sheet name, capitalized (the sheet stores common nouns lowercase
  because the game capitalizes at render time). The mount → item reverse index
  is built once on first use; the `Mount` sheet has no back-reference.
  Registered with the game's command manager, so it works from the native chat
  box and macros, and it completes names in our input like `/examine`. Named
  `/mountid`, not `/mount`: the latter is the game's own summon command, and a
  Dalamud handler on that name shadows it.
- **"Try On Original"** in the game's own item context menus — the native Try
  On previews the item as glamoured and dyed, which is precisely what you are
  trying to see past. This one passes zero for both. Glamour lives on the
  inventory item (plates write it onto each equipped piece), so `GlamourId`
  covers prism and plate glamours alike, and the entry only appears when there
  is something to strip: on an unglamoured item it would be a byte-identical
  duplicate of the native entry. Equippable items only, bottom of the menu,
  toggleable in settings.

### Added 2026-07-28 — translation (v0.3.0)
- **Translate tab in settings.** Off by default; first enable shows a
  confirmation naming what leaves the machine (tells included).
- **Backends**: Google/Bing/Yandex via GTranslate (no account, the default),
  DeepL with an API key, Anthropic, or any OpenAI-compatible endpoint
  (Ollama/LM Studio/OpenRouter). Optional automatic fallback to the free
  backends when the chosen one fails, is out of quota, or is rate limiting.
- **Incoming**: translated text replaces the body in its own color; hovering
  shows the original under a "JA → English" header. Only lines a player typed
  are eligible (system/NPC text is game-written and already localized), and a
  channel grid narrows that further. Requests batch (250 ms), dedupe identical
  lines, and cache 2000 results.
- **Outgoing**: typed input is translated before sending, asynchronously —
  the input stays usable, and a failure hands the draft back rather than
  sending the wrong thing. Text starting with `/` is never translated.
- **On demand**: right-click any message → Translate / Show original / Copy
  Text, ignoring every filter, so translation can stay off day to day.
- **Rate limits**: a 429 is never retried inline. It starts a cooldown —
  `Retry-After` when given, else 1 min doubling per repeat strike, capped at
  15 min — during which the provider is left alone and the fallback covers.

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

### Added 2026-07-12 (v0.2.0)
- **Item linking end to end** — inventory "Link" inserts `<item>` into our
  input (vanilla append/replace semantics); the placeholder renders blue
  inline via a transparent-InputText overlay repaint with its own caret, and
  a preview line above the input names the staged item. Mechanism notes in
  PLAN.md; the `<item>` string arrives in the 0x0C activation's AtkValues.
- **Native item tooltips + context menu** — hovering an item link opens the
  game's own ItemDetail tooltip (positioned beside the window: native UI
  always renders under ImGui, overlap would hide it; hard-clamped on
  screen). Left/right click opens a vanilla-style menu: Try On, Item
  Comparison, Search for Item, Search Recipes, Link, Copy Name. Toggle in
  settings; custom card stays as fallback.
- **Role colors + job icons in party chat** — sender job resolved from the
  party list at capture time, persisted per message (`sender_job`); rendered
  as tank/healer/DPS prefix color + framed job icon, both optional.
- **Game chat keybinds honored** — Alt+R reply, Alt+P party, Alt+F FC,
  say/yell/shout/alliance/PvP/novice, LS/CWLS 1-8 (+ "always" twins), read
  live from the game's keybind config. Works with and without our input
  focused (dual poll: framework-update via KeyState, draw-time via ImGui —
  Dalamud suppresses game keys while ImGui captures). Channel binds also
  select the matching tab (dedicated tab first, else a game-channel tab).
- **QoL round** — clickable URLs; duplicate-message collapse (×N, option);
  12/24h timestamp display; per-channel color overrides; history search
  (History settings tab, own read connection); retention setting + DB size;
  auto-hide rules (cutscene/UI-hidden defaults on, loading/combat opt-in);
  settings restructured into General/Colors/History tabs.
- **Fixes** — FC-only tabs hidden while the character has no FC; tells no
  longer leak into catch-all tabs when unticked from General/All (channel
  grid is the "what shows in General" control, combined All inherits it).

### Added 2026-07-19
- **Emote shortcodes** — Discord-style `:sob:` in any message renders as a
  Twemoji image (text-height, hover shows a larger preview + the shortcode).
  Fully self-contained: the shortcode map (gemoji's alias set, ~1900 names,
  the same ones Discord uses) and all 1869 referenced Twemoji 72×72 PNGs
  ship as embedded resources (`Data/emotes.json` + `Data/emotes.zip`,
  ~1.7 MB) — nothing is ever downloaded. Textures are created lazily per
  emote from the in-memory zip and disposed on unload. Parsing is a
  `MessageParser` post-pass, so live capture and DB hydration both get it;
  the sent text stays a plain `:sob:` for others. Typing `:` + 2 chars pops
  the existing suggestion popup with emoji icons (prefix matches before
  substring, Tab/click accepts, mid-sentence safe — the entry carries the
  whole replacement buffer). Toggle in settings ("Chat display"), default
  on. Regenerating the assets: gemoji `emoji.json` → alias map, Twemoji
  release tarball → zip of the mapped codepoint files (name rule: hex
  codepoints joined by `-`, U+FE0F dropped unless the sequence has a ZWJ).
- **Fix: FC tab no longer vanishes in duties** — the auto-hide trusted
  the nameplate `CompanyTag`, which the game blanks inside instanced
  content. Membership is now read from `InfoProxyFreeCompany` (its name
  field is fed by the zone-init packet — the same reason FC chat keeps
  working in duties), with the tag as fallback; a positive result
  latches until logout so transient unreadable states can't hide the
  tab.
- **Cleanup round (multi-agent audit)** — three audits (native-API
  usage, dead/duplicated code, render-path perf), findings verified
  against the shipped Dalamud/ClientStructs docs, then applied:
  - *Better APIs*: outgoing chat now runs Dalamud's `Sanitizer` (the
    game's own sanitation pass) before the plugin's control-char strip;
    the local player name comes from `IPlayerState.CharacterName`
    (cached managed string) instead of a per-frame SeString parse.
    Confirmed-good: no Dalamud API exists for keybind reading
    (`UIInputData` stays), shared game-icon textures are interned by
    Dalamud (per-frame `GetFromGameIcon` is the intended pattern).
  - *Perf, behavior-identical*: word tokens cached per segment,
    timestamps/prefixes/sender-links cached per message, per-tab
    message snapshots cached by revision, tab labels cached, tab-order
    sync early-outs without allocating, FC proxy read stops once
    latched, input placeholder scan runs once per frame. Eliminates
    the ~6-10k allocations/frame the log cost at its 500-message cap.
  - *Dedup/dead code*: shared `GameData` (world/item lookups that
    existed in three/two copies), `ChatTypes.Mask` (the `& 0x7F` in
    four places), one friend-proxy reader (was two), one fixed-tab
    routing path (was two), settings checkbox helper (13 blocks),
    line-wrap helper; removed unread `MessageStore.Revision` and
    unused `CommandEntry.FromPlugin`; stale `TabOrder`/`ClosedTellTabs`
    ids now pruned at load (an accepted-debt item since the audit).

- **Clean-code round (two Fable reviewers, services + UI)** — applied:
  chat-send failures now log instead of vanishing (the send runs detached
  on the framework thread); emote zip reads guarded like every sibling
  failure mode; captured messages no longer walk their SeStrings twice;
  URLs shed glued sentence punctuation ("see https://a.com." no longer
  404s, balanced-bracket aware); flagged chat-type variants (source bits
  in the high byte) now color/prefix like their base kind, matching how
  routing already treats them; history search debounced 300 ms (was a
  full-table LIKE scan per keystroke); "message too long" now notifies
  instead of Enter silently doing nothing; closed tell tabs drop their
  drafts (no ghost drafts on reopen); the last bare hook detour got the
  try/catch the other two had; nearby-only context items each explain
  their disabled state; two stale doc comments corrected (TabManager
  threading contract, MessageDatabase read paths). MainWindow god-class
  verdict: acceptable as a window controller; if it keeps growing, the
  tell-integration hooks (~250 lines) and tab-strip scroll/order sync
  (~180 lines) are the two clean seams to extract first.

## Known gaps / accepted quirks
- Friend-list refresh runs every 60 s → presence dot can lag up to a minute
  after opening a tab or a friend logging off.
- Cross-world party invites from the context menu may fail (no content id
  available from chat payloads); Add Friend / Blacklist not implemented.
- 300 ms capture dedup can swallow a genuinely repeated identical message
  (visually mitigated by the ×N duplicate collapse).
- Enter interception itself still assumes the default Enter key; the chat
  *channel* keybinds are now read from the game config.
- ChatLog activation hook is signature-based — a game patch can kill it
  (plugin still loads; vanilla input focus just stops being suppressed).
  The ItemDetail tooltip open pokes two unnamed agent bytes — equally
  patch-fragile (see NativeItemTooltip.cs).
- Reply keybind opens the most recent tell tab; no partner cycling like
  vanilla's forward/back rotation. LS/CWLS *rotation* binds skipped.
- Native tooltip slides behind the chat when the window leaves no side room
  (layering limit: game UI renders under ImGui, unavoidable).
- Older audit notes in PLAN.md still apply (CWLS shared colors).
- A single unbroken over-long word (e.g. a huge URL) never word-wraps —
  the tokenizer splits on spaces only; the row overflows instead. Both
  review passes flagged it; fix needs mid-word measuring, deferred.

## Next up
- v3 feature round complete (see PLAN.md). Candidates for next: reply
  partner cycling, richer custom tooltip card (stats/materia) for the
  full-width-window case, ChatTwo-style input preview line.

## State of the tree
- Public at https://github.com/P529/FF14Chat; latest release **v0.3.4.0**
  (2026-08-16): "Try On Original" on examined gear, the Ctrl+right-click
  try-on shortcut, and the Tweaks settings tab.
- **v0.3.3.0** (2026-08-07): `/mountid` and "Try On Original", plus the README
  rewritten around the shipped feature set.
- **v0.3.2.0** (2026-08-05): sent lines keep their channel, the log lands on
  the newest line, tab slots hold. Earlier: **v0.3.1.0**, **v0.3.0.0**
  (translation), **v0.2.3.x** fixes.
- Older note, kept for the release mechanics — latest release **v0.2.1**
  (2026-07-12): hidden tabs (FC tab without a free company, or during the
  login frames where CompanyTag reads empty) no longer lose their saved
  position — SetOrder used to sort absent tabs to the end and persist it.
- **v0.2.0** (2026-07-12): the v3 feature round (item linking, native
  tooltips + context menu, role colors/job icons, game keybinds, QoL
  batch) — details in "Added 2026-07-12" above.
- Previous release **v0.1.2** (2026-07-11). Releases are cut by pushing a
  `v*` tag matching the csproj `<Version>`; users on the custom-repo URL
  auto-update in game.
- Post-v0.1.1 fixes in v0.1.2: programmatic tab switching to earlier-drawn
  tabs (backward Shift+Tab, wrap-around) never applied — `selectTabId` is now
  consumed-on-apply and fixed tabs receive the SetSelected flag too; history
  recall no longer reopens the suggestion popup (which stole Up/Down from
  walking further back).
