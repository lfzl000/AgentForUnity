# Agent for Unity

Agent for Unity is an Editor-only UPM package that connects a Unity project to a local `codex app-server` process.

Release `0.3.0` implements the M1 core workflow plus conversation and message-presentation improvements.

The core workflow provides:

- locate and start the local Codex CLI app server;
- detect and install the Unity CLI beta channel;
- install `com.unity.pipeline` with Unity 2022.3 source adaptation when required;
- install Pipeline skills and the project Unity guide after Pipeline setup;
- initialize the protocol and read account state;
- load the account's available models and reasoning efforts;
- start or recover a project-scoped workspace-write thread;
- list and switch between resumable Codex conversations created for the current Unity project;
- stream Agent messages, plans, reasoning summaries, tools, commands, and file changes;
- attach Project, Selection, recent Console, File, Scene, and Git Diff context;
- attach clipboard images or the current Game view as removable screenshots with thumbnail previews;
- approve or decline command, network, file-change, and user-input requests;
- choose a persistent Ask Approval, Codex Decides, or Full Access permission mode;
- inspect the current turn's aggregated Diff and open changed files;
- track Unity compilation and continue compiler-error repair in the same thread;
- restore persisted context, Diff, compile state, and the selected thread after Domain Reload;
- steer or interrupt the active turn.

## Requirements

- Unity 2022.3 LTS or newer
- Codex CLI `0.144.0` or later with an authenticated account
- macOS for the currently supported environment

## Install

For a local checkout, install this repository's `package.json` through **Window > Package Manager > Add package from disk**.

To install the current `main` branch from Git, use **Window > Package Manager > Add package from git URL** with:

```text
https://github.com/lfzl000/AgentForUnity.git#main
```

The GitHub repository is private, so Git must be authenticated for the current user before Unity can install it.

After installation, open **Window > Agent for Unity**. The package version should show as `0.3.0` in Package Manager.

The package depends on `com.unity.nuget.newtonsoft-json` `3.2.1`.

The **Unity Tooling** panel reports Unity CLI, Pipeline package, and authenticated Pipeline Server reachability status. **Install Pipeline** is the one-click setup path on Unity 2022.3 and Unity 6 or newer: it installs Unity CLI first when missing, installs the Pipeline package, applies the source compatibility patch on Unity 2022.3, and then installs the package's `unity-pipeline` skill under `.agents/skills`. Unity 2022.3 projects also receive the `unity-pipeline-2022` compatibility skill. The setup preserves an existing `UNITY-GUIDE.md` and adds the required guide instruction to `AGENTS.md` only when it is missing.

## Use The Window

In Unity, choose **Window > Agent for Unity**. The window reports the detected CLI, account, project root, current thread, connection state, and diagnostics. The **Conversations** pane lists non-archived threads whose working directory is the current Unity project; use **New Thread**, refresh, or select a thread to continue it. A thread that is already active in another Codex client opens read-only.

Choose an available model, reasoning effort, and permission mode before sending a prompt. Attach Project, Selection, specific Console entries, File, Scene, or Git Diff context as required. The first new thread automatically receives the project summary. Context is redacted, de-duplicated by hash, and constrained by size budgets before it is sent.

Use **+ Screenshot** to attach an image from the clipboard or capture the current rendered Game view. Press **Cmd+V** on macOS while the prompt is focused to attach the clipboard image directly. Screenshot drafts and sent messages show thumbnail previews; remove a draft with the **x** button on its preview. Images are stored under `Library/AgentForUnity/Attachments` and sent to Codex as local image inputs rather than imported Unity assets.

Agent responses render common Markdown. Plans, reasoning summaries, tool actions, command output, and file-change activity are grouped with the Agent message for the same turn. Use **Stop** to interrupt an active turn. While a turn is running, **Send** becomes **Steer** and appends a correction to that turn.

When Codex requests additional filesystem or network access during a turn, Agent for Unity displays an approval card instead of rejecting the protocol request. **Allow Once** grants the requested access for the current turn; **Allow Session** uses the App Server session scope; decline grants no additional access.

The details pane contains approval requests, compilation feedback, the current turn Diff, connection metadata, and diagnostics. Changed-file links in Markdown and the Diff open project-relative files. After a completed turn with a recorded file Diff, the package requests Unity script compilation. The resulting status is compilation feedback only and must not be treated as Play Mode or visual validation. If Unity does not start compiling, select **Compile Unity**; when compilation fails, **Continue Fix** sends the captured error summary to the same thread.

An active conversation keeps Unity's assembly reload locked so its App Server process remains connected. When **Reload Domain** is enabled, Agent for Unity blocks entering Play Mode during an active conversation because the reload would terminate that turn. To allow automated Play Mode checks during a conversation, enable **Enter Play Mode Options**, disable **Reload Domain**, and keep **Reload Scene** enabled.

## Permission Modes

- **Ask Approval**: uses Codex's `on-request` approval policy with a user reviewer and a writable sandbox limited to the Unity project.
- **Codex Decides**: uses `on-request` approval with that project-scoped writable sandbox and network access disabled by default.
- **Full Access**: uses `never` approval with Codex's `danger-full-access` sandbox. This mode is unrestricted.

Codex owns authentication and conversation history. Agent for Unity does not write API keys, OAuth tokens, or message history to `Assets`. Reload state, redacted context drafts, turn Diff, and compilation state are stored under `Library/AgentForUnity/`.

## Package Layout

```text
Editor/
  Application/   M1 application state, Unity context, compile monitoring, and view models
  Process/       Codex process lifecycle and CLI detection
  Protocol/      JSON-RPC and app-server protocol handling
  UI/            UI Toolkit editor window and Markdown renderer
```
