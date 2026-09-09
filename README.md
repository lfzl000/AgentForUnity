# Agent for Unity

Agent for Unity is an Editor-only UPM package that embeds a Codex client in Unity. It uses UI Toolkit and communicates with a local `codex app-server` process, so authentication, models, conversations, tool execution, and permission decisions remain owned by Codex.

Release `0.3.0` implements the M1 workflow plus project-scoped conversation browsing and switching, activity placement within the corresponding response, Markdown rendering for Agent messages, CLI discovery, and post-turn compilation requests.

## Requirements

- Unity 2022.3 LTS or newer
- Codex CLI `0.144.0` or later
- An authenticated Codex account
- macOS for the currently supported environment

## Use

Open the project in Unity and choose **Window > Agent for Unity**. The package detects the local CLI, account, and project root, then lists resumable conversations for that project. Start a new thread or open an existing one, select the model, reasoning effort, and permission mode, attach any needed Unity context, and send the request.

Agent responses render common Markdown, and their associated tool, plan, reasoning-summary, command, and file-change activity is grouped with the response that produced it. Use **Stop** to interrupt a turn or **Steer** to add direction while one is running. The details pane keeps approvals, the current turn Diff, compile state, connection details, and diagnostics available.

After a completed turn that changes files, the package requests Unity script compilation and records the observed result. This is compilation feedback only; it is not Play Mode or visual validation. When Unity does not start compilation, use **Compile Unity** to request it explicitly. A failed compilation can be sent back to the same conversation with **Continue Fix**.

## Permissions And Data

The selected permission mode applies to new turns:

- **Ask Approval** uses an untrusted approval policy with a project-scoped writable sandbox.
- **Codex Decides** uses on-request approval with a project-scoped writable sandbox and network access disabled by default.
- **Full Access** disables approval prompts and uses Codex's unrestricted sandbox. Select it only when that scope is intended.

Context attachments can include Project, Selection, selected Console entries, File, Scene, and Git Diff. Context is redacted, hashed for duplicate detection, and size-limited before it is sent. The package stores reload state and redacted context drafts under `Library/AgentForUnity/`; it does not store API keys, OAuth tokens, or full conversation history in `Assets`.

## Install In Another Project

The embedded package is located at [`Packages/com.zlr.agentforunity`](Packages/com.zlr.agentforunity). See its [package README](Packages/com.zlr.agentforunity/README.md) for usage and package details.

To install release `0.3.0` in another project, use **Window > Package Manager > Add package from git URL**:

```text
https://github.com/lfzl000/AgentForUnity.git?path=/Packages/com.zlr.agentforunity#v0.3.0
```

The repository is private, so Git must be authenticated for the current user.

## Product Scope

The product requirements and milestone definitions are documented in [`Docs/Product/AgentForUnity-MVP-PRD.md`](Docs/Product/AgentForUnity-MVP-PRD.md).
