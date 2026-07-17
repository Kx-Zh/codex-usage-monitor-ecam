# Codex Usage Monitor — ECAM v1.0.1

This maintenance release fixes CTX tracking for Codex Desktop tasks and removes
Windows user-path assumptions.

## Changes

- Tracks the currently focused Codex Desktop task from the latest
  `thread_stream_view_activity_changed active=true` log event.
- Recursively searches both `sessions` and `archived_sessions` for the matching
  conversation JSONL.
- Falls back to the latest main task while excluding `subagent` sessions.
- Refreshes CTX every 5 seconds independently of the 60-second account usage
  query and preserves the last valid CTX value through temporary read errors.
- Honors `CODEX_HOME`, otherwise resolving `.codex` from the current Windows
  user profile.
- Supports traditional Codex Desktop log locations and dynamically discovered
  Microsoft Store package directories.

The UI layout is unchanged. The release ZIP contains the monitor only; it does
not contain Codex CLI, credentials, or user session data.
