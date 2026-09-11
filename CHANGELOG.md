# Changelog

All notable changes to Agent for Unity are documented in this file.

## [0.7.0] - 2026-09-11

### Fixed

- Git-installed packages now update through Unity Package Manager resolution instead of attempting to pull the read-only Package Cache, and version checks bypass cached GitHub manifests.

## [0.6.0] - 2026-09-11

### Added

- A manual update-check button beside the package update status.

### Fixed

- Package updates are no longer blocked by ordinary Unity Tooling refreshes, and disabled update buttons now identify the actual blocking operation.

## [0.5.0] - 2026-09-11

### Added

- Checks the Agent for Unity GitHub package manifest when the window opens and offers a one-click, fast-forward-only update for clean local Git checkouts.

## [0.4.0] - 2026-09-11

### Fixed

- Object and Console context menus now follow Agent for Unity's interface language, showing **Add to AgentForUnity** in English and **添加到 AgentForUnity** in Chinese.
- Console context menus now receive mouse events from the actual IMGUI host in Unity 2022.3 and Unity 6, instead of attaching to the separate UI Toolkit content root.
- Prevented entering Play Mode from triggering Domain Reload while a conversation is active, avoiding termination of its App Server process and an unrecoverable in-flight turn.
- Active turns now receive a stable-protocol Play Mode restriction when Reload Domain is enabled, so the final response explains why required runtime validation was skipped without requiring experimental App Server capabilities.
- Installed official Pipeline workflows now request approved local execution on their first localhost call instead of reporting an expected restricted-sandbox failure first.
- Tooling connectivity probes are now event-driven instead of running every ten seconds, preventing Unity 2022 Mono file-descriptor exhaustion during long Editor sessions.
- Tooling package installation now explicitly requests Unity Package Manager resolution, allowing package import and script compilation to start automatically after an external CLI updates the manifest.

### Added

- **添加到 AgentForUnity** context menus for Hierarchy objects, Project assets, Inspector components, and selected Console logs, with removable context attachments and support for successive additions and mid-turn steering.
- Git availability notices that hide Git controls when the project has no repository or Git cannot be used, and restore them after detection succeeds.
- Large Commit All changes now generate messages from bounded change statistics and file metadata instead of timing out while reading a multi-megabyte patch.
- Background commit-message generation now uses `gpt-5.6-luna` with low reasoning effort, independently of chat settings.
- Git Pull, Push, and Commit All actions in Project Changes, with ephemeral background commit-message generation from project rules and a checked repository snapshot.
- Enter Play Mode settings, current reload behavior, and contextual guidance in the Unity Tooling panel.
- Direct screenshot capture from the current Unity Game view.
- Approval cards for App Server filesystem and network permission requests, with one-turn and session grant scopes.
- Version-aware Unity tooling setup: Unity CLI Loop for Unity 2022.3 through pre-Unity 6 projects, and the official Unity CLI with `com.unity.pipeline` by default on Unity 6 or newer.
- An explicit Unity 6 backend selector for switching between the official Pipeline backend and Unity CLI Loop when no turn is active.
- Unity CLI Loop CLI, package, connection, and project skill setup through `hatayama/unity-cli-loop`.

### Changed

- Removed the Unity 2022.3 Pipeline source adaptation and its compatibility skill; supported pre-Unity 6 Editors now use Unity CLI Loop without modifying official Pipeline source.
- Existing Unity CLI Loop V2 installations are treated as upgrade candidates instead of valid active backends.
- Tooling activation now completes only after the selected backend's CLI, package, skills, and managed Unity guide are ready, then reconnects the App Server with the active instructions.
- Existing packages and skills from another backend are preserved on disk but remain disabled while that backend is inactive.
- Backend switches now show the active and selected sources separately and block new turns until the switch is applied or cancelled.
- Legacy Unity 2022 adapted `Packages/com.unity.pipeline` copies are rejected with a migration prompt instead of being activated as official packages.

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
