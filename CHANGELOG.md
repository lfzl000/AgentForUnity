# Changelog

All notable changes to Agent for Unity are documented in this file.

## [Unreleased]

### Fixed

- Prevented entering Play Mode from triggering Domain Reload while a conversation is active, avoiding termination of its App Server process and an unrecoverable in-flight turn.
- Active turns now receive a stable-protocol Play Mode restriction when Reload Domain is enabled, so the final response explains why required runtime validation was skipped without requiring experimental App Server capabilities.
- Fixed Unity 2022 Pipeline adaptation so its watchdog refreshes and recreates missing instance descriptors, keeping CLI discovery available while the server is healthy.
- Installed Pipeline workflows now request approved local execution on their first localhost call instead of reporting an expected restricted-sandbox failure first.

### Added

- Enter Play Mode settings, current reload behavior, and contextual guidance in the Unity Tooling panel.
- Direct screenshot capture from the current Unity Game view.
- Approval cards for App Server filesystem and network permission requests, with one-turn and session grant scopes.
- Unity CLI detection and installation, plus one-click `com.unity.pipeline` setup with package and authenticated server reachability status.
- Automatic Unity 2022.3 Pipeline source adaptation and project-scoped Pipeline skill/guide installation.

## [0.3.0] - 2026-09-09

### Added

- Clipboard screenshot attachments with removable composer thumbnails and sent-message previews.
- A project-scoped list of resumable conversations with status, last-updated time, refresh, and thread switching.
- Markdown rendering for Agent responses, including project-relative file links.
- Per-message activity presentation for plans, reasoning summaries, tools, commands, and file changes.
- A post-turn Unity script-compilation request for turns with a recorded Diff, plus a manual Compile Unity action when no compilation starts.

### Changed

- CLI discovery now selects the newest compatible candidate from `CODEX_EXECUTABLE`, `PATH`, and macOS fallback locations; the minimum supported Codex CLI version is `0.144.0`.
- Conversation switching opens a thread read-only when another Codex client is its active writer.
- The chat layout keeps conversation browsing, the composer, approval state, Diff, compilation state, and diagnostics available together.

## [0.2.0] - 2026-09-09

### Added

- M1 workspace-write turns with command, network, and file-change approval cards scoped to their thread, turn, and item.
- Streamed plan, reasoning-summary, command-output, file-change, and tool activity cards.
- Removable Project, Selection, Console, File, Scene, and Git Diff context attachments with redaction, hashing, and size budgets.
- Aggregated turn Diff panel with changed-file navigation.
- Unity compilation status and result cards with same-thread error continuation.
- Persistence for context drafts, turn Diff, compilation state, and Domain Reload recovery.
- Mid-turn steering from the message composer.
- A persistent Ask for Approval, Approve for me, and Full Access permission selector.

### Changed

- Turns now apply the selected Codex approval policy and sandbox; Approve for me remains project-scoped and Full Access is explicitly unrestricted.
- Package documentation and metadata now describe the M1 workflow.

## [0.1.0] - 2026-09-08

### Added

- UI Toolkit editor window for project-scoped Codex conversations.
- Codex CLI discovery and `0.144.x` compatibility validation.
- App Server initialization, account discovery, and paginated model loading.
- Read-only thread creation, recovery, streaming turns, and interruption.
- Project-local thread and model metadata persistence without conversation content or credentials.
- EditMode coverage for protocol parsing, process coordination, CLI detection, and persisted state.
