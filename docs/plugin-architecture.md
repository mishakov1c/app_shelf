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

Install `artifacts/plugins/NotesPlugin-1.0.0.asplugin` through the plugin manager. CI also publishes the package as a separate artifact. Enable it explicitly, open Notes from plugin tools, type a note and click Save. The settings window can enable a host notification after each save; notifications are polled and may be coalesced or suppressed by Windows. Closing the notes window hides it; unsaved text remains only in memory until the process exits. The two declared data files are notes.txt and settings.txt, with opt-in backup disabled by default.

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
