# Agent for Unity

Agent for Unity is an Editor-only UPM package that connects a Unity project to a local `codex app-server` process.

This `0.1.0` package implements the M0 protocol feasibility milestone:

- locate and start the local Codex CLI app server;
- initialize the protocol and read account state;
- load the account's available models and reasoning efforts;
- start or recover a project-scoped thread;
- send a read-only turn and stream Agent text;
- interrupt the active turn;
- expose connection diagnostics without storing credentials in the project.

Approval UI, Unity context attachments, file changes, command execution, Diff presentation, and compile-result feedback are intentionally outside M0.

## Requirements

- Unity 2022.3 LTS or newer
- Codex CLI `0.144.x` with an authenticated account
- macOS for the first validated M0 environment

## Install

Install `Packages/com.zlr.agentforunity/package.json` as a local package, or add the package to a Unity project's `Packages` directory.

To install `0.1.0` from Git, use **Window > Package Manager > Add package from git URL** with:

```text
https://github.com/lfzl000/AgentForUnity.git?path=/Packages/com.zlr.agentforunity#v0.1.0
```

The GitHub repository is private, so Git must be authenticated for the current user before Unity can install it.

The package depends on `com.unity.nuget.newtonsoft-json` `3.2.1`.

## Open

In Unity, choose **Window > Agent for Unity**. The window reports the detected CLI, account, project root, current thread, connection state, and any diagnostics. Select an available model and reasoning effort, then start a thread and send a prompt.

Codex owns authentication and conversation history. Agent for Unity does not write API keys, OAuth tokens, or message history to `Assets`.

## Package Layout

```text
Editor/
  Application/   M0 application service and view models
  Process/       Codex process lifecycle and CLI detection
  Protocol/      JSON-RPC and app-server protocol handling
  UI/            UI Toolkit editor window
```
