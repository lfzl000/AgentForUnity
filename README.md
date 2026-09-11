# Agent for Unity

[简体中文](README.zh-CN.md)

Agent for Unity is an Editor-only Unity package that connects your project to a local Codex agent. Use it from Unity to discuss work, send project context, review changes, and approve requests.

## Requirements

- Unity 2022.3 LTS or newer
- macOS or Windows
- Codex CLI `0.144.0` or later, signed in to a Codex account

## Install

Install a local checkout with **Window > Package Manager > Add package from disk**, then select this repository's `package.json`.

The window checks the GitHub package manifest for a newer version and shows **Update** when one is available. Git packages are refreshed through Unity Package Manager; local Git checkouts use a fast-forward-only `git pull`, so commit or stash local package changes first.

Or install the latest `main` branch with **Add package from git URL**:

```text
https://github.com/lfzl000/AgentForUnity.git#main
```

The package depends on `com.unity.nuget.newtonsoft-json` `3.2.1`.

## Get Started

1. Open **Window > Agent for Unity**.
2. Complete the Unity Tooling setup shown in the window. Unity 2022.3 through pre-Unity 6 uses Unity CLI Loop; Unity 6 can also use the official Unity CLI backend.
3. Select a model, reasoning effort, and permission mode.
4. Start a conversation and send your request.

The window streams the agent response and associated activity. Use **New Thread** to begin another conversation, or select a previous conversation for the current project to resume it. An active conversation in another Codex client opens read-only.

## Context And Media

Attach Project, Selection, Console entries, files, scenes, or Git Diff as context before sending. Right-click selected objects, components, or Console entries and choose **Add to AgentForUnity** to add them to the composer.

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

After a turn records file changes, Agent for Unity can request script compilation and show the result. Compilation feedback is not Play Mode or visual validation.

## Privacy And Storage

Codex manages authentication and conversation history. Agent for Unity does not write API keys, OAuth tokens, or message history into `Assets`. Temporary state and media attachments are stored under `Library/AgentForUnity/`.

## Package Layout

```text
Editor/
  Application/   State, Unity context, compilation, and view models
  Process/       Codex process lifecycle and CLI detection
  Protocol/      JSON-RPC and App Server protocol handling
  UI/            UI Toolkit window and Markdown renderer
```
