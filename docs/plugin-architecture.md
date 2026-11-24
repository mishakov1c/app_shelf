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
- Windows child process with redirected stdin/stdout/stderr; stderr is drained without retaining private text. Each process is assigned to a non-inherited, kill-on-close Job Object immediately after Process.Start. If assignment fails, terminate rather than allow an unsupervised plugin. Trusted plugins must not create child processes before hello; this is lifecycle containment, not a security sandbox. Host closure, including update, closes jobs. A normal shutdown requests shutdown and bounds the wait; on timeout the job is closed. Replacement/removal require confirmed process exit; data remains separate.
- API 1 adds ping, successful empty response (apiVersion/id/ok). Every 15 seconds the host checks running plugins, skips an overlapping operation, and marks a crashed/unresponsive plugin as an error, without automatic restart. Plugin operations serialized off UI; package commit obtains same reservation after trust confirmation, not during the modal prompt. Updates disable new version; cancelled update leaves previous process untouched. If package swap fails after stop, previous version may remain enabled but stopped; manually start it.
- Manager shows actual runtime state, explicit start/restart, and persists enable/disable as before. During an action the window is temporarily busy. Core locale is ru-RU until localization release. No command menu or notification delivery yet.
- Unhandled host termination closes the job handle; there is a small process creation-to-job-assignment window because this implementation uses managed Process.Start, not suspended Win32 creation. This is not a protection from malicious same-user code. Crash-safe package swap recovery remains to be implemented before beta.
