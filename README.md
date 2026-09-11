# Agent for Unity

Agent for Unity is an Editor-only UPM package that connects a Unity project to a local `codex app-server` process.

Release `0.3.0` implements the M1 core workflow plus conversation and message-presentation improvements.

The core workflow provides:

- locate and start the local Codex CLI app server;
- detect the Unity version and select the supported Unity Editor tooling backend;
- install either the official Unity CLI with `com.unity.pipeline` or Unity CLI Loop;
- install the selected backend's skills and update the project Unity guide after setup;
- initialize the protocol and read account state;
- load the account's available models and reasoning efforts;
- start or recover a project-scoped workspace-write thread;
- list and switch between resumable Codex conversations created for the current Unity project;
- stream Agent messages, plans, reasoning summaries, tools, commands, and file changes;
- attach Project, Selection, recent Console, File, Scene, and Git Diff context;
- attach clipboard images or the current Game view as removable screenshots with thumbnail previews;
- approve or decline command, network, file-change, and user-input requests;
- choose a persistent Ask for Approval, Approve for me, or Full Access permission mode;
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

The GitHub repository is public, so Unity can install it directly from the Git URL.

After installation, open **Window > Agent for Unity**. The package version should show as `0.3.0` in Package Manager.

The package depends on `com.unity.nuget.newtonsoft-json` `3.2.1`.

The **Unity Tooling** panel selects the Unity Editor tooling backend from the current Editor version:

| Unity version | Default backend | Selection |
| --- | --- | --- |
| Earlier than 2022.3 | Unsupported | None |
| 2022.3 or newer, earlier than Unity 6 | [Unity CLI Loop](https://github.com/hatayama/unity-cli-loop) | Required; cannot switch to the official Pipeline backend |
| Unity 6 or newer | Official Unity CLI with `com.unity.pipeline` | May switch explicitly between the official backend and Unity CLI Loop |

Agent for Unity does not automatically fail over to the other backend when a health check fails. On Unity 6, changing the selection is an explicit operation and is allowed only when no turn is active. After selecting a different backend, new turns remain disabled until setup activates it or the switch is cancelled. If setup fails, selecting the previous backend cancels the switch only when that backend's package and skills are still ready; otherwise setup must be retried.

For the official backend, setup installs the Unity CLI beta channel, `com.unity.pipeline`, and the `unity-pipeline` skills. For Unity CLI Loop, setup follows the upstream `uloop package install` flow to add `io.github.hatayama.uloopmcp` through OpenUPM, installs the V3 `uloop` CLI from [hatayama/unity-cli-loop](https://github.com/hatayama/unity-cli-loop), and installs its common `.agents/skills`. A V2 CLI or package is treated as requiring the upstream V3 install flow instead of being activated. Unity 2022.3 through pre-Unity 6 projects use Unity CLI Loop directly; Agent for Unity no longer downloads or modifies official Pipeline source to make it compile there.

The selected backend becomes active only after its CLI, package, skills, and managed `UNITY-GUIDE.md` section are ready. Agent for Unity then reconnects the App Server so new and resumed turns receive the active instructions, including the requirement to read and follow `UNITY-GUIDE.md` for Unity development. Setup preserves existing project files and does not modify `AGENTS.md` to add this requirement. Packages, CLIs, and skills from a previously used backend are not removed automatically; their presence does not make them active, and the managed guide disables every backend except the selected one.

If an older Agent for Unity setup left a modified Unity 2022 copy at `Packages/com.unity.pipeline`, the official backend refuses to activate it. Back up any local changes, remove that embedded directory, and retry setup so Unity CLI can install the unmodified official package.

## Use The Window

In Unity, choose **Window > Agent for Unity**. The window reports the detected CLI, account, project root, current thread, connection state, and diagnostics. The **Conversations** pane lists non-archived threads whose working directory is the current Unity project; use **New Thread**, refresh, or select a thread to continue it. A thread that is already active in another Codex client opens read-only.

Choose an available model, reasoning effort, and permission mode before sending a prompt. Attach Project, Selection, specific Console entries, File, Scene, or Git Diff context as required. The first new thread automatically receives the project summary. Context is redacted, de-duplicated by hash, and constrained by size budgets before it is sent.

Use **+ Screenshot** to attach an image from the clipboard or capture the current rendered Game view. Press **Cmd+V** on macOS while the prompt is focused to attach the clipboard image directly. Screenshot drafts and sent messages show thumbnail previews; remove a draft with the **x** button on its preview. Images are stored under `Library/AgentForUnity/Attachments` and sent to Codex as local image inputs rather than imported Unity assets.

Agent responses render common Markdown. Plans, reasoning summaries, tool actions, command output, and file-change activity are grouped with the Agent message for the same turn. Use **Stop** to interrupt an active turn. While a turn is running, **Send** becomes **Steer** and appends a correction to that turn.

When Codex requests additional filesystem or network access during a turn, Agent for Unity displays an approval card instead of rejecting the protocol request. **Allow Once** grants the requested access for the current turn; **Allow Session** uses the App Server session scope; decline grants no additional access.

The details pane contains approval requests, compilation feedback, the current turn Diff, connection metadata, and diagnostics. Changed-file links in Markdown and the Diff open project-relative files. After a completed turn with a recorded file Diff, the package requests Unity script compilation. The resulting status is compilation feedback only and must not be treated as Play Mode or visual validation. If Unity does not start compiling, select **Compile Unity**; when compilation fails, **Continue Fix** sends the captured error summary to the same thread.

An active conversation keeps Unity's assembly reload locked so its App Server process remains connected. When **Reload Domain** is enabled, Agent for Unity blocks entering Play Mode during an active conversation because the reload would terminate that turn. To allow automated Play Mode checks during a conversation, enable **Enter Play Mode Options**, disable **Reload Domain**, and keep **Reload Scene** enabled.

## Permission Modes

- **Ask for Approval**: uses Codex's `on-request` approval policy with a user reviewer and a writable sandbox limited to the Unity project.
- **Approve for me**: uses `on-request` approval with automatic review, that project-scoped writable sandbox, and network access disabled by default.
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
