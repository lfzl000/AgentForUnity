# Agent for Unity

[简体中文](README.zh-CN.md)

Agent for Unity is an Editor-only Unity package that connects your project to a local Codex agent. Use it from Unity to discuss work, send project context, review changes, and approve requests.

## Requirements

- Unity 2022.3 LTS or newer
- macOS or Windows
- Codex CLI `0.144.0` or later, signed in to a Codex account
- The release package includes the Agent Bridge runtime for the host platform. Source checkouts can run the Bridge through the installed `dotnet` SDK.

The package currently includes a macOS Apple Silicon (`osx-arm64`) Bridge runtime. If the current platform has no packaged runtime, Agent for Unity falls back to the original Codex App Server transport and shows a diagnostic. In fallback mode, an active conversation can be interrupted when Unity reloads the scripting domain.

## Install

Install a local checkout with **Window > Package Manager > Add package from disk**, then select this repository's `package.json`.

The window checks the GitHub package manifest for a newer version and shows **Update** when one is available. Git packages are refreshed through Unity Package Manager; local Git checkouts use a fast-forward-only `git pull`, so commit or stash local package changes first.

Or install the latest `main` branch with **Add package from git URL**:

```text
https://github.com/lfzl000/AgentForUnity.git#main
```

The package depends on `com.unity.nuget.newtonsoft-json` `3.2.1`.

### First use

Before connecting, verify that `codex --version` reports `0.144.0` or later and sign in with the Codex CLI. Source checkouts also require a .NET 9 SDK when no packaged Bridge runtime is available. Open the window and click **Connect**. Complete **Unity Tooling** setup when you need scene inspection or Editor operations; text and file conversations can work without it.

The Bridge uses a local loopback port and temporary state under `Library/AgentForUnity/`. Firewall or endpoint-security software must allow the local connection.

## Get Started

1. Open **Window > Agent for Unity**.
2. Complete the Unity Tooling setup shown in the window. Unity 2022.3 through pre-Unity 6 uses Unity CLI Loop; Unity 6 can also use the official Unity CLI backend.
3. Select a model, reasoning effort, and permission mode.
4. Start a conversation and send your request.

The window streams the agent response and associated activity. Scroll up to read earlier messages while a reply is still streaming; auto-follow pauses until you click **Back to latest** or return to the bottom. Use **New Thread** to begin another conversation, or select a previous conversation for the current project to resume it. An active conversation in another Codex client opens read-only.

## Context And Media

Attach Project, Selection, Console entries, files, scenes, or Git Diff as context before sending. Right-click selected objects, components, or Console entries and choose **Add to AgentForUnity** to add them to the composer.

Selection context includes a bounded Unity Object Snapshot with stable object IDs, scene and hierarchy location, component types, serialized fields, Prefab information, missing-script markers, and object references. Click the context preview button to inspect the exact redacted payload that will be sent. Console context added from the Console menu also includes bounded source excerpts and matching scene component snapshots when stack frames can be resolved.

Snapshots are intentionally bounded. When a requested field is omitted, Agent for Unity can use the active Unity Tooling backend to query that object by its Global Object ID or project path. If Unity Tooling is not enabled and connected, on-demand object, component, Prefab, and scene inspection is unavailable; Unity Editor operations and validation are also unavailable. The Tooling panel and completed-turn message both show an explicit **Enable and connect Unity Tooling** prompt. Text and file-based work can continue.

Use **+ Media** to attach an image from the clipboard or capture the Game view. With the prompt focused, press **Cmd+V** on macOS or **Ctrl+V** on Windows to attach a clipboard image. Media attachments stay outside `Assets` and can be removed before sending.

For recordings, copy a local video file and paste it into the prompt, or choose **+ Media → Clipboard Recording**. macOS and Windows file clipboard formats are supported; macOS can also read raw movie clipboard data. Supported file extensions: `.mp4`, `.mov`, `.m4v`, `.webm`, `.mkv`, `.avi` (up to 512 MiB per recording; the first video is attached when multiple files are copied). A recording can be sent without text. Click its attachment card to play it in the default application. Removing a draft deletes only the managed copy, never the original. Sent copies remain available for conversation history.

Recordings are sent as local file context with their path, format, and size. The current App Server input schema has no native video input: analysis requires the agent to use available media tools or extract frames, subject to file access permissions. Video frames and audio are not automatically uploaded or transcribed.

While a turn is running, **Send** becomes **Steer** so you can add direction. Use **Stop** to interrupt the turn.

## Permissions

| Mode                 | Behavior                                                                                               |
| -------------------- | ------------------------------------------------------------------------------------------------------ |
| **Ask for Approval** | You approve requested access.                                                                          |
| **Approve for me**   | Requests are automatically reviewed within the project sandbox; network access is disabled by default. |
| **Full Access**      | Unrestricted Codex access.                                                                             |

When more file-system or network access is required, the window presents an approval card. You can allow it once, allow it for the App Server session, or decline it.

## Git And Compilation

The **Project Changes** panel provides **Pull**, **Push**, and **Commit All** for the repository containing the Unity project. Review changes before using these actions; committing does not push automatically.

With Agent Bridge, compilation can run during an active conversation. After a turn records file changes, Agent for Unity can also request script compilation and show the result. Compilation feedback is not Play Mode or visual validation. Without Agent Bridge, compilation waits until the turn completes so Domain Reload cannot interrupt the session.

## Bridge behavior

With a matching packaged runtime, the Bridge owns the Codex App Server process outside Unity. Conversations can survive script recompilation, Domain Reload, and Play Mode transitions, and the Play Mode settings section is hidden.

Without a matching runtime, the package uses the legacy App Server process. The Play Mode settings section remains visible and explains that Reload Domain can interrupt an active conversation.

## Privacy And Storage

Codex manages authentication and conversation history. Agent for Unity does not write API keys, OAuth tokens, or message history into `Assets`. Temporary state and media attachments are stored under `Library/AgentForUnity/`.

## Package Layout

```text
Editor/
  Application/   State, Unity context, compilation, and view models
  Process/       Codex process lifecycle and CLI detection
  Protocol/      JSON-RPC and App Server protocol handling
  UI/            UI Toolkit window and Markdown renderer
Bridge~/
  Program.cs     External session host used to keep Codex alive across Unity Domain Reload and Play Mode
```
