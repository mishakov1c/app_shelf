# NetworkPlugin

Public standalone AppShelf API v1 plugin. Preserves the two configurable checks (default ICMP + HTTPS with TCP fallback, ICMP, HTTPS, TCP), eight-second background monitor, selected Windows route, manual DNS/HTTPS and local interface/proxy/DNS report. Three indicators can be added with the host's existing indicator picker. Manual diagnostics are available even with background monitoring off.

Settings are stored only in the plugin data directory in `network-settings.json`. No registry or adapter writes, no Happ recovery actions. The existing host monitoring remains active until a subsequent migration commit; do not enable both for the same targets during tests.

Build with `Build-Plugins.cmd --no-open --no-pause` from the repository root, or `Build.ps1` in this directory. Initial package is not a release. Host migration and automatic indicator placement remain to be implemented and verified before removing built-in monitoring.
