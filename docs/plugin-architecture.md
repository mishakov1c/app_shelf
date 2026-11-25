# AppShelf plugin architecture — package layer (2.24.0, work in progress)

Approved 24.11.2026: separate processes, language-independent protocol; existing tools remain built in. The host owns its UI; future tiles/widgets are described by data and actions, not arbitrary native controls. Do not load plugin DLLs into the host.

## Stage 1: local packages

`.asplugin` is a ZIP archive with `plugin.json` at its root and executable/dependencies in `bin/`. No code executes during extraction or validation. Example:

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "entryPoint": "bin/Hello.exe",
  "description": "Example plugin",
  "capabilities": ["command", "window"]
}
```

IDs are stable, lowercase ASCII, reverse-domain-like; versions are numeric X.Y.Z. The installer rejects unsupported schema/API, unsafe paths, links, duplicate paths, oversized packages, missing entry points and unknown capabilities. Extraction is staged; replacing an installed version requires a greater version, and a failed directory replacement attempts rollback. Installed code and user data are separate under the existing profile root: `Plugins/Installed/<id>` and `Plugins/Data/<id>`. Uninstall removes code only. No arbitrary plugin code is loaded into the AppShelf process. Only install trusted packages; an independent process is not a security sandbox. No network/catalog/authentication mechanism at this stage.

## Next stages (not implemented by this change)

- Manager UI: list packages, inspect publisher/source and capabilities, explicit trust confirmation, installation disabled until enabled; update/uninstall must first stop the plugin, recover interrupted updates and preserve data.
- Host protocol: versioned, bounded messages over authenticated local IPC with process lifetime and cancellation; separate foreground commands/windows and background lifecycle; settings and notifications. Define handshake and errors before enabling execution. Never expose arbitrary process-launch or file-access APIs via IPC.
- Private modules installed manually without publication; public catalog and online update later. Localization and portable paths must not depend on WPF-specific types. Explicit backup opt-in for plugin data; no implicit inclusion of clipboard history.
- Demonstration plugin, Windows regression and manual acceptance before beta publication.

Stage 1 intentionally does not start any plugin, change user settings, or modify the existing updater/backup formats.

## Stage 2: package manager

The window is accessible through the main tools menu and Settings → Plugins. File picker and single-file drag/drop use the same staged installation. Trust is requested after validating the exact staged bytes; the declared publisher is not verified. New installations and updates are disabled by default. Approval and enabled state are host-created marker files in the installed directory; they cannot be supplied in a package. Update-by-row requires matching plugin ID. Uninstall preserves data. Invalid installed entries show errors and can be removed only if the path is a valid non-linked tree. Window size uses the existing window-size service. No process starts, including for enabled packages. Markers are application state, not a security boundary against code running as the same user. Interrupted-update recovery and full runtime integration remain future work.

## Stage 3: process protocol (API 1)

Host creates the child process with redirected standard input/output. Use binary streams, not line readers/writers. stdin is host → plugin, stdout plugin → host; stdout MUST contain only protocol frames, stderr is diagnostics (the process owner must drain it, without logging private content by default). No TCP listener, shared pipe name, DLL, or platform-specific object in the protocol. The inherited private handles bind the conversation to the spawned process; this is not a sandbox or cryptographic publisher verification. Never expose a general file or command execution service through the host.

Frame: 4-byte unsigned little-endian length followed by exactly that many UTF-8 JSON bytes, no BOM. Payload 1–65536 bytes. Every request has apiVersion (1), id (positive, increasing within session), method. Every response echoes apiVersion and id, contains ok (boolean). Failure carries error (nonempty, ≤1000 UTF-16 code units); no result fields. Unknown additive JSON fields may be ignored. Exactly one outstanding request; no unsolicited frames, no stdout logs. The host imposes deadlines and rejects wrong IDs, versions, oversized/truncated frames, malformed messages and inconsistent results. Fatal protocol/IO errors or in-flight timeout/cancellation invalidate the session; process owner must kill/close the owned process and drain/close streams. Never retry on the same session. A normal method error is not fatal except hello/shutdown. Commands and notices are plain text, not markup.

### Methods

- hello: first request. Includes pluginId, pluginVersion, locale (e.g. ru-RU), dataDirectory (absolute host-selected path). Response must echo pluginId and pluginVersion from the manifest, optionally commands (up to 32 objects with id and label). Command IDs are lowercase ASCII, 1–64 characters, start with a letter, then letters/digits/underscore/dot/hyphen. Labels 1–100 characters. Commands require command or window capability. The plugin can initialize but must not start background work until requested.
- invoke: commandId from hello response. Plugin opens/activates its window or performs the command, then acknowledges promptly; do not wait for user to close a window.
- settings: requires settings capability. Open/activate plugin-owned settings window, acknowledge promptly.
- startBackground: requires background capability, once per session. Start background work, acknowledge promptly.
- poll: requires notification capability. Returns optional notifications array (at most 16 entries, title ≤100, message ≤1000 characters). No push messages in API 1. Host polling cadence, display/rate limiting, error backoff and foreground activation are runtime work.
- shutdown: request cooperative exit, acknowledge first, then exit. Host enforces exit deadline.

Example handshake (JSON payloads; actual frames include length prefix):

```json
{"apiVersion":1,"id":1,"method":"hello","pluginId":"example.hello","pluginVersion":"1.0.0","locale":"ru-RU","dataDirectory":"C:\\Example\\Plugins\\Data\\example.hello"}
{"apiVersion":1,"id":1,"ok":true,"pluginId":"example.hello","pluginVersion":"1.0.0","commands":[{"id":"open","label":"Open"}]}
```

Stage 3 supplies the codec/session only. The process owner, process cleanup, UI binding, background polling, notification delivery, hotkeys, backup and sample plugin are not active yet. API 1 is provisional until the first accepted beta.

## Stage 4: process lifecycle

- Enabled background-capable packages start at AppShelf load or when enabled; other enabled packages wait for explicit start. Runtime revalidates the installed package and enabled/trust markers immediately before launch. Manual restart is explicit; no automatic crash/restart loop.
- Windows child process with raw redirected stdin/stdout/stderr; stderr is drained without retaining private text. Win32 CreateProcess starts suspended, passing only the three child pipe handles. The process joins a non-inherited kill-on-close Job Object before resuming. If assignment fails, terminate rather than allow an unsupervised plugin. This is lifecycle containment, not a security sandbox. Host closure, including update, closes jobs. A normal shutdown requests shutdown and bounds the wait; on timeout the job is closed. Replacement/removal require confirmed process exit; data remains separate.
- API 1 adds ping, successful empty response (apiVersion/id/ok). Every 15 seconds the host checks running plugins, skips an overlapping operation, and marks a crashed/unresponsive plugin as an error, without automatic restart. Plugin operations serialized off UI; package commit obtains same reservation after trust confirmation, not during the modal prompt. Updates disable new version; cancelled update leaves previous process untouched. If package swap fails after stop, previous version may remain enabled but stopped; manually start it.
- Manager shows actual runtime state, explicit start/restart, and persists enable/disable as before. During an action the window is temporarily busy. Core locale is ru-RU until localization release. No command menu or notification delivery yet.
- Unhandled host termination closes the job handle. A suspended process cannot execute plugin code before job assignment; the raw binary channels avoid the .NET Framework stdin StreamWriter preamble. This is not a protection from malicious same-user code. Crash-safe package swap recovery remains to be implemented before beta.

## Interrupted package updates

Before moving installed code, the installer flushes staged files and approval, then writes and flushes a small journal via a same-directory rename. It records plugin ID, old/new version and a random transaction token; paths are derived from the validated ID/token. The runtime reservation and a shared package commit lock serialize the swap with discovery and recovery. On the next discovery (before starting enabled background plugins) or direct package access, a journal is resolved: old target intact → discard staging; target absent with valid previous → restore previous; validated new target in place → finish old-tree cleanup. A partially deleted previous tree is safe to clean only when the new target is validated. Uncertain/malformed/conflicting states are retained and reported as disabled error rows; no execution or removal of those packages until recovery succeeds. Never infer an update from unassociated staging/previous directories. Plugin data is not touched. This is not a version rollback feature or a guarantee against storage-device corruption/power-loss write-cache behavior. Existing packages without a journal remain compatible. Tests simulate interruption at each rename and during cleanup.

## Stage 5: plugin commands, settings and notifications

The main “Плагины” submenu lists only enabled compatible packages with command/window/settings capability and is hidden when empty. Management is available only through Settings → Plugins → Manage plugins. Opening the main menu refreshes the package list without starting processes. Running plugins with one known command open it directly; multiple known commands form a nested menu. For idle plugins, the first explicit activation discovers commands (one opens immediately, multiple use a chooser until the next menu opening). Settings are available in the manager; settings-only plugins can open settings directly from the main menu. Both manager and menu use the same runtime. The runtime checks the expected installed version before dispatch and validates registered IDs, so stale menu items cannot invoke different versions. Remote method errors are shown without treating the entire session as crashed; protocol/IO faults stop the process. Plugin windows remain plugin-owned, not embedded WPF controls.

Health checks also poll running notification-capable plugins. Polling is pull-only. At most one notice per plugin per 15 seconds and eight per batch are accepted; excess notices are dropped. The host has a bounded queue of 12 and displays tray notices at least eight seconds apart, skipping stopped/replaced plugins, with a status-line fallback if the tray icon is unavailable. Titles and messages are plain text and truncated to 63/255 UTF-16 code units for the Windows balloon. No notification history or delivery guarantee; Windows may suppress balloons. Existing reminders and sounds are unchanged. Hotkeys, custom widgets, and background notification delivery to PocketHub are outside this stage.

## Stage 6: backups

All existing full ZIP backups (manual, daily, before core update) include a bounded plugin inventory (ID, version, name, former enabled state) and explicitly opted-in declared data files. Packages, executables, approval markers and undeclared data are never included. The manifest optionally declares backupFiles (relative portable file paths, max 64, no wildcards). The user must enable data backup separately for each plugin; default off. Only existing declared files are captured, with 4 MiB per file and 8 MiB raw total, 16 MiB serialized maximum. A change during capture/export invalidates the backup instead of committing a partial snapshot. No encryption. Live plugin data must tolerate file-level snapshots; a transactional database snapshot protocol is not part of API 1.

Restoring a ZIP validates and stores the inventory/data as a recovery document without installing, enabling or changing live plugins. Older archives leave the document unchanged. The manager shows missing plugins for manual reinstall and offers an explicit restore only for the same version. The runtime stops the plugin before applying data. Only matching declared files are restored; other files are unchanged. Current affected files are saved under the selected backup root before replacement; ordinary write errors trigger rollback. Restored plugins remain stopped until manually launched or next AppShelf startup. This is a two-step recovery by design, not automatic restoration of executable code or a full plugin database migration. The user should retain original .asplugin packages separately. The recovery document is replaced by a later full ZIP restore and the previous document is retained in its before-restore copy.

Consent is bound to the installed version and exact sorted backupFiles list; a new version needs explicit opt-in again. Uninstall removes consent, not data. Explicit data restoration leaves the plugin disabled, including after failure, to avoid automatic use of partly restored files. No implicit execution or trust is restored from a ZIP.

## Full program backup (revised scope)

Full manual, daily and pre-core-update ZIP backups default to including Program/AppShelf.exe and Plugins/Packages/<id>.asplugin. Both can be disabled independently in backup settings. A package is reconstructed from its manifest and bin tree, including dependencies; trust/enabled markers are not included. Opt-in data policy remains independent. Packages are bounded to 128 MiB each and 512 MiB total compressed package bytes, core to 256 MiB. Binary entries are validated structurally, not authenticated; never execute unknown backups. Automatic fingerprints include selected program bytes. Normal restore restores settings and stages plugin data; separate extraction selects core and/or packages into a new folder, never replaces the running EXE. Install packages with renewed trust; an older package requires removal of the newer package first (data retained). Before plugin update, save the previous .asplugin in configured backup root/BeforePluginUpdate; absent/failed backup blocks replacement. These previous packages are independent of automatic retention; separate pruning is future work. The existing pre-core-update EXE remains independently available even if ZIP executable inclusion is disabled. ZIP contents are unencrypted.

## Stage 7: standalone notes example

The separately built example lives in `samples/NotesPlugin`. It uses Windows Forms in its own executable, with the binary API 1 frame codec compiled from source. The manifest declares window, settings, background and notification capabilities. Nothing installs automatically. From the repository root, double-click `Build-Plugins.cmd`. It builds sample projects that contain both `plugin.json` and `Build.ps1`, writes packages to `artifacts/plugins/` and opens that folder on success. For unattended builds use `Build-Plugins.cmd --no-open --no-pause`. The host build remains independent. A failed build returns a nonzero exit code and may leave previously built packages intact. For a single plugin, its original PowerShell build script is also available.

Install `artifacts/plugins/NotesPlugin-1.0.1.asplugin` through the plugin manager. CI also publishes the package as a separate artifact. Enable it explicitly, open Notes from plugin tools, type a note and click Save. The settings window can enable a host notification after each save; notifications are polled and may be coalesced or suppressed by Windows. Closing the notes window hides it; unsaved text remains only in memory until the process exits. The two declared data files are notes.txt and settings.txt, with opt-in backup disabled by default.

Acceptance on an isolated Windows profile: verify disabled installation, enable, open/reopen both windows, save Russian and multiline text, restart and confirm persistence; enable save notification, save again and check the host notification. Enable plugin data backup, create a ZIP with packages, remove the plugin, modify or remove the note data, restore the ZIP (no live installation), extract the package, reinstall with renewed trust, explicitly restore same-version data, then enable and verify the original note. Check the plugin remains disabled immediately after restore and that closing AppShelf stops its child process. Never test destructive restore against the only copy of real user data.

The backup reliability regression builds and installs this actual sample, invokes commands/settings and health checks, roundtrips package and declared data through ZIP recovery, and verifies disabled restore and restart. It seeds the documented text files directly; interactive editing, native window presentation and tray notification display require manual acceptance. Stage 7 is not considered accepted until Windows CI and manual checks pass.


## Stage 8: declarative widget API (provisional API 1 extension)

The manifest may declare the new `widget` capability. Existing API 1 packages without it keep working; no new requests are sent to them. Older hosts reject packages with the unknown capability. No change to framing, trust, lifecycle or backup formats.

A running, explicitly enabled widget-capable plugin accepts `getWidgets`. The host requests a whole snapshot rather than accepting pushed messages or executing UI code. Success includes an explicit `widgets` array (empty means none), with at most 8 descriptors per plugin. Widget IDs are stable within the plugin and use the same syntax as command IDs; IDs are unique in a snapshot. Title ≤100 characters. Host placement will use plugin ID + widget ID, never the display name. Supported kind hints: `card`, `tile`, `indicator`. Each descriptor has 1–12 flat elements (indicator: 1–2). All labels are nonempty plain text ≤240 UTF-16 units. Each element has exactly the fields appropriate to its type:

| type | additional field | meaning |
| --- | --- | --- |
| text | none | informational text in label |
| metric | value (nonempty string ≤100 units) | label/value pair |
| progress | percent (finite number 0–100) | progress indicator |
| action | commandId | registered hello command, executed only on a user click |

Example request and response (illustrative prices, not real market quotes):

```json
{"apiVersion":1,"id":2,"method":"getWidgets"}
{"apiVersion":1,"id":2,"ok":true,"widgets":[{"id":"quotes","title":"Курсы акций","kind":"card","elements":[{"type":"text","label":"Демо · условные данные"},{"type":"metric","label":"AAPL","value":"$200.00"},{"type":"metric","label":"MSFT","value":"$400.00"},{"type":"action","label":"Открыть","commandId":"open"}]}]}
```

Here open must already be advertised by hello under command/window capability. The host must render literal data, never parse as XAML/HTML, and dispatch actions via the existing version-checked invoke path. Invalid snapshot fields, duplicates, unknown types/actions, missing arrays, unexpected result fields and oversized messages fail the protocol. The existing 64 KiB frame bound and deadlines apply. Ordinary remote errors do not invalidate a healthy session. The runtime checks installed version and trust/enabled state; refresh never launches or restarts a stopped plugin. A renderer must explicitly activate a user-placed plugin or rely on its background policy.

This stage implements only manifest acceptance, DTOs, request/response validation and runtime snapshot access; no visual renderer, placement settings, polling scheduler, stock data provider, arbitrary controls, images, remote assets, or widget-driven host APIs yet. Rendering, freshness/error states, bounded refresh scheduling, and a demonstrator are follow-up stages. No widget snapshot is persisted by this API. The protocol and real-process regression tests cover old packages, valid snapshots, actions, stale versions and invalid responses.


## Stage 9: host-owned WPF widget renderer

`PluginWidgetView.Create(widget, invoke)` builds a native WPF Border from an already validated API snapshot. It uses the AppShelf theme brushes and ordinary WPF controls. Cards have a wider layout and aligned label/value pairs, tiles use a narrow stacked layout with larger metric values, and indicators use compact wrapping elements. Width hints are upper bounds (440, 200, 360 WPF device-independent units respectively); the parent container controls actual placement and width. No plugin markup, URLs, executable paths or custom controls are interpreted.

Actions use the command ID from the validated element and an injected asynchronous callback. All buttons in the view are temporarily disabled while an action is pending, duplicate clicks are ignored, and errors are contained with an inline message. Without a callback, actions are disabled. The future host controller must bind this callback to the existing version-checked runtime invoke method, invalidate stale views after disable/update/removal and avoid rendering unvalidated snapshots.

The renderer is included in the .NET Framework and SDK project builds. The isolated WPF regression exercises the three layouts, literal text, progress values, action dispatch and failure, narrow width and captures a preview. The main-window placement controller, persisted layout, refresh scheduler, stale/error states for snapshots and actual bottom-panel integration are not implemented by this stage. No widget is inserted into an existing user layout automatically.


## Stage 10: widget workspace and explicit placement (session scope)

The main Add menu includes “Виджет плагина…”. The picker lists enabled, valid widget-capable packages without starting them. Loading the selected package's widgets is an explicit activation; users then choose a card or tile. The workspace has a wrapping strip above the existing application-group host inside the main scroll viewer. It collapses when empty. Placement identity is plugin ID + widget ID, one placement per identity, up to 32 placements per session. Distinct widgets from a single plugin can be added. Right-click a placement to refresh or remove it; removal does not uninstall or stop the plugin. The workspace does not change the application-group layout.

The runtime captures snapshot/process identity together under its operation gate. Actions revalidate the enabled package, version, live process identity and command under that gate, and never start a replacement process. A removed or replaced view invalidates its callbacks. The existing health check and plugin manager closure invalidate widgets from stopped/replaced processes; no automatic snapshot requests yet. Explicit refresh is non-activating and updates a placed descriptor only if the same ID and kind are available. Failed refresh replaces old content with an unavailable state.

This stage is intentionally session-local: positions, sizing and persistence, automatic snapshot scheduling, indicator placement in the bottom panel, and a dedicated stock demonstration package remain follow-up work. No user layout or settings format migration yet. The isolated WPF tests cover insertion, duplicate rejection, refresh, stale callbacks, invalidation and removal; the real-process tests verify explicit activation and process-bound action dispatch.


## Stage 11: widget groups and per-size layouts

This supersedes the standalone strip of independently sized cards. Widget groups have a host-owned 2x2 grid with four rectangular sizes:

| size | columns x rows |
| --- | --- |
| full | 2 x 2 |
| half-horizontal | 2 x 1 |
| half-vertical | 1 x 2 |
| quarter | 1 x 1 |

The API 1 snapshot optionally adds `layouts` (1–4 unique objects, each containing `size` and `elements`) and `defaultSize`. With layouts present, only declared sizes are supported and the default must be one of them. Each layout is subject to the same element/command validation, and the complete frame still has the 64 KiB bound. Base `elements` remains required for compatibility. Without layouts, the same base elements may be rendered in any of the four sizes; default is full for card and quarter for tile, unless overridden by a valid defaultSize. Indicators cannot declare group layouts. The selected size is a host preference; changing size does not send a command or require starting the plugin.

Example widget (illustrative data):
```json
{"id":"quotes","title":"Курсы акций","kind":"card","elements":[{"type":"metric","label":"AAPL","value":"$200.00"}],"defaultSize":"half-horizontal","layouts":[{"size":"quarter","elements":[{"type":"metric","label":"AAPL","value":"$200.00"}]},{"size":"half-horizontal","elements":[{"type":"metric","label":"AAPL","value":"$200.00"},{"type":"metric","label":"MSFT","value":"$400.00"}]}]}
```

Adding uses the first free rectangle in existing groups or creates a new group. A widget's right-click menu offers its supported sizes that fit in the current group, moving to a different group with free space, or creating a new group. Resize/move never overlap another widget; the host removes empty groups. Each group's cell area currently measures 280 device-independent units in height and up to 400 in width (adapted to the viewport). Excess content scrolls within its allocated area. Group backgrounds and borders follow the existing AppShelf group appearance settings. Existing app-group rendering and storage are unchanged.

The first implementation keeps widget groups in their own wrapping container above app groups. Unified ordering across app and widget groups, drag/drop, group collapse/rename, user-controlled group dimensions, persistence/backup of placements, automatic refresh and indicator placement are follow-up work. Placements remain session-local. Tests cover all spans, collision prevention, per-size data, moving to a new group, removing empty groups, stale commands and invalid API layouts. The SiteLinks menu regression now expects all four actual Add choices instead of the old three.


## Stage 12: widget groups share the free layout board

Widget groups are visible only when grouping is enabled and the selected layout is free. The Add menu keeps its widget entry disabled in other modes; direct activation is guarded too. Switching layouts detaches widget cards from the free board without destroying their content or changing plugin enabled state. Returning to free layout attaches the existing groups to the nine ordinary zone stacks, with widget group width constrained to the same zone limit. No separate strip remains. Right-click the group heading to rename, collapse/expand, move above/below adjacent groups (including application groups), or choose a zone. Drag the group heading to a different free-board zone. Group order is currently a slot index in the zone; application-group ordering remains unchanged. The usual group appearance applies.

This is still session-local; persistence and automatic widget refresh will follow. Dragging individual widgets between cells and lower-panel indicators are not yet included. WPF tests cover attachment, relative order, rename, collapse, zone change and mode switching.


## Stage 13: persistent widget placement

Optional `widgetGroups` in apps.json stores group number, title, zone, order, collapse state, and each widget's plugin ID, widget ID, last known version/title, size and cell coordinates. It contains no executable data, live values, commands, enabled state or trust. Configuration copy is deep for these fields. Config load/save and full ZIP validation reject invalid group identities, out-of-range positions, duplicate widgets and overlaps rather than silently rewriting a malformed layout. Older configurations without the field remain valid.

Placement changes use the ordinary configuration save path; a failed save reloads the last valid placement metadata. Full ZIP export includes the field through apps.json. Restored settings construct inactive placeholders even if plugins are missing/disabled; loading the layout alone performs no RPC or plugin start. The user can explicitly refresh an unavailable widget to activate the currently installed, enabled, trusted version. Refresh revalidates the version and process; no old command callbacks survive replacement. Saved layout is independent of the selected app-group mode.

Automatic widget refresh, dragging individual widgets and bottom-panel indicators are follow-up work.


## Stage 14: bounded automatic widget refresh

The existing 15-second runtime health tick also schedules widget data refresh when the main window is visible, not minimized, and the free board is active. After background startup an initial refresh is attempted. Each tick handles up to four distinct plugin IDs round-robin; a single validated snapshot feeds all their placements. The workspace skips overlapping batches and plugins with an in-flight widget command or refresh. Identical visible content does not rebuild controls. A missing widget or unsupported selected size displays a placeholder; a failed read does not expose stale values as current.

An inactive persisted placement may request one startup activation of its currently enabled/trusted package when the board first becomes visible. This requires the regular package approval/enabled checks and is not permission to enable or install a package. The runtime rejects automatic activation of an errored or exited process under its operation gate. Subsequent periodic reads are non-activating, version-bound to the running child. Manual refresh remains an explicit activation path for unavailable placements. Hidden layouts, minimized windows and closed hosts do not initiate widget refreshes; no new timer or uncontrolled retry loop. Live snapshots are not persisted.

Tests cover batch deduplication, unchanged content, hidden layouts and rejection of failed-process auto-restart.


## Stage 15: offline Stocks widget demonstration

`samples/StocksPlugin` is an independent process using only the API protocol and simulated numbers (no API keys, network or real stock data). It demonstrates a card named “Курсы акций · демо” with all four size variants, a second compact Apple widget with two supported sizes and a future lower-panel indicator. Every snapshot advances the deterministic demo sequence; an explicit “Следующий пример” command advances it further. The package declares widget/command, not background, so it starts on explicit activation or approved restored placement. The titles identify the numbers as demo data.

Double-click `Build-Plugins.cmd` to build Notes and Stocks packages into artifacts/plugins. Install `StocksPlugin-1.0.0.asplugin` disabled, enable it in the manager, select free layout, then Add → Widget plugin → load the Stocks demo. Try resizing, moving, adding the second widget, changing layout modes, restarting and checking periodic updates. CI builds and uploads the demo and runs a separate isolated regression of the actual package and lifecycle; the test builds its own fresh copy even in workflows that do not build samples first. A live quotation provider is outside the demo scope.


## Stage 16: direct widget drag and drop

Drag an individual widget by its title onto a free rectangle inside any expanded widget group, including the source group. The pointer snaps to the valid span origin inside the 2x2 grid. An accent outline previews an accepted destination; occupied/out-of-bounds rectangles reject the drop. The existing host drag routing distinguishes widget drags, group drags, application tiles and external files. Moves preserve widget size, save through the existing layout path and remove emptied source groups. The context menu remains an alternative for moving to a new group. The test verifies exact coordinates, span preservation, collision rejection and source-group cleanup; manual drag gestures still need Windows acceptance.


## Stage 17: plugin indicators in the bottom panel

Right-click the bottom panel and choose “Добавить индикатор плагина…”. The picker filters for the existing indicator kind, separately from the free-layout card/tile picker. The indicator strip is collapsed when empty and otherwise appears in all three footer presentation modes, regardless of whether the application groups are in free layout. It uses a horizontally scrolling bounded row (up to six explicitly selected indicators). Right-click an indicator to refresh or remove it.

The optional apps.json `widgetIndicators` list stores the same identity/version/title metadata with size indicator and zero row/column. It participates in validation, deep configuration copy and full ZIP backup/restore. Indicators and group widgets cannot share a duplicate plugin/widget identity. Restored indicators show placeholders until a trusted enabled plugin supplies a live snapshot. They share the existing periodic refresh batch with group widgets; hidden group placements are not refreshed just because an indicator is present, but one plugin snapshot can serve both visible surfaces. No new plugin capability or API version is required. The offline Stocks demo advertises an indicator for testing this path.


## Stage 18: manual acceptance UI corrections

Free placement retains nine named zones but measures and arranges each vertical column independently. Top/middle/bottom stacks share available height only within their own column. Overflow extends the scrollable board without pushing groups in other columns down; narrow windows retain the stacked nine-zone view. Widget groups shrink to one row when no placement occupies the bottom row, temporarily expanding both rows during widget drag. Drag handles on group and widget headers expose the interaction; valid destinations are outlined while dragging. Widget group headers have a count and an explicit collapse/expand button, and the context-menu commands remain available.

Shared dark selection resources now apply to WPF ComboBox, ComboBoxItem, ListBox, ListBoxItem and ScrollBar controls in the application. Existing specialized reminder/calendar and settings resources remain in place. The source audit covers the main executable's compiled UI files; visual acceptance of all individual dialogs is still required. Native Windows dialogs and standalone WinForms plugin windows are not restyled by host WPF resources.

Regressions cover independent column overflow, compact widget group height, collapse/expand button, and shared selection controls. No plugin protocol or stored layout schema changes.
