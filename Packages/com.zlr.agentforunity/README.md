# Agent for Unity

Agent for Unity is an Editor-only UPM package that connects a Unity project to a local `codex app-server` process.

This `0.2.0` package implements the M1 core workflow:

- locate and start the local Codex CLI app server;
- initialize the protocol and read account state;
- load the account's available models and reasoning efforts;
- start or recover a project-scoped workspace-write thread;
- stream Agent messages, plans, reasoning summaries, tools, commands, and file changes;
- attach Project, Selection, recent Console, File, Scene, and Git Diff context;
- approve or decline command, network, file-change, and user-input requests;
- choose a persistent Ask Approval, Codex Decides, or Full Access permission mode;
- inspect the current turn's aggregated Diff and open changed files;
- track Unity compilation and continue compiler-error repair in the same thread;
- recover context, Diff, compile state, and the current thread after Domain Reload;
- steer or interrupt the active turn.

## Requirements

- Unity 2022.3 LTS or newer
- Codex CLI `0.144.x` with an authenticated account
- macOS for the currently supported environment

## Install

For a local checkout, install `Packages/com.zlr.agentforunity/package.json` through **Window > Package Manager > Add package from disk**, or add the package to a Unity project's `Packages` directory.

To install `0.2.0` from Git, use **Window > Package Manager > Add package from git URL** with:

```text
https://github.com/lfzl000/AgentForUnity.git?path=/Packages/com.zlr.agentforunity#v0.2.0
```

The GitHub repository is private, so Git must be authenticated for the current user before Unity can install it.

After installation, open **Window > Agent for Unity**. The package version should show as `0.2.0` in Package Manager.

The package depends on `com.unity.nuget.newtonsoft-json` `3.2.1`.

## Open

In Unity, choose **Window > Agent for Unity**. The window reports the detected CLI, account, project root, current thread, connection state, and any diagnostics. Select an available model, reasoning effort, and permission mode, then start a thread and send a prompt. Ask Approval runs only trusted commands automatically, Codex Decides lets Codex request elevation when needed, and Full Access disables approval prompts and the sandbox.

Codex owns authentication and conversation history. Agent for Unity does not write API keys, OAuth tokens, or message history to `Assets`. Reload state and redacted context drafts are stored under `Library/AgentForUnity/`.

## Package Layout

```text
Editor/
  Application/   M1 application state, Unity context, compile monitoring, and view models
  Process/       Codex process lifecycle and CLI detection
  Protocol/      JSON-RPC and app-server protocol handling
  UI/            UI Toolkit editor window
```
