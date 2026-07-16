# Codex Usage Monitor — ECAM v1.0.0

Initial public release of the Windows-native Codex ECAM Monitor.

## Highlights

- Always-on-top ECAM-inspired remaining-usage display.
- Numeric background tray icon with color thresholds and show/hide controls.
- Current rate-limit period, local reset time, and reset-credit count.
- `CTX K` quantity and 240-degree context-window arc.
- Codex CLI discovery through `CODEX_CLI_PATH`, a legacy sibling
  `codex.exe`, or the Windows `PATH`.
- Complete English and Simplified Chinese documentation.

## Installation

Download and extract `CodexEcamMonitor-v1.0.0-win-x64.zip`, then run
`Start Codex ECAM Monitor.bat`.

Codex CLI is **not included**. Install and authenticate the official CLI first:

https://github.com/openai/codex

The accompanying `.sha256` file can be used to verify the ZIP before
extraction.
