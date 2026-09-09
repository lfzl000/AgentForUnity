# Agent for Unity

Agent for Unity is an Editor-only UPM package that embeds a Codex client in Unity. The package uses UI Toolkit and communicates with a local `codex app-server` process.

The current `0.2.0` release implements milestone M1: the M0 connection and thread foundation plus Unity context attachments, streamed activity, command/file approvals, turn Diff presentation, compile-result feedback, mid-turn steering, and Domain Reload recovery.

## Requirements

- Unity 2022.3 LTS or newer
- Codex CLI `0.144.6` or a compatible release
- An authenticated Codex account
- macOS for the currently supported environment

## Open The Window

Open the project in Unity and choose **Window > Agent for Unity**.

The embedded package is located at [`Packages/com.zlr.agentforunity`](Packages/com.zlr.agentforunity). See its [package README](Packages/com.zlr.agentforunity/README.md) for usage and package details.

To install release `0.2.0` in another project, use **Window > Package Manager > Add package from git URL**:

```text
https://github.com/lfzl000/AgentForUnity.git?path=/Packages/com.zlr.agentforunity#v0.2.0
```

The repository is private, so Git must be authenticated for the current user.

## Product Scope

The product requirements and milestone definitions are documented in [`Docs/Product/AgentForUnity-MVP-PRD.md`](Docs/Product/AgentForUnity-MVP-PRD.md).
