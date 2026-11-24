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
