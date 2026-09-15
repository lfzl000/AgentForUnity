# Agent Bridge

`AgentForUnity.Bridge` is a small external host for the Codex App Server. It
keeps the App Server process alive while Unity recompiles or enters Play Mode;
the Unity package remains the client and continues to use the installed Unity
Tool for Editor and scene operations.

## Start

From the package repository root:

```text
dotnet run --project Bridge~/AgentForUnity.Bridge.csproj -- \
  --project-path /path/to/unity-project \
  --codex-path /absolute/path/to/codex
```

The checked-in project targets .NET 9 for source development. Release builds
should publish self-contained binaries under `Bridge~/runtimes/<rid>/` so Unity
users do not need a separate .NET SDK.

The bridge binds to `127.0.0.1` on an ephemeral port by default and writes its
discovery information to
`<project>/Library/AgentForUnity/bridge.json`. Pass `--state-path` or
`--port` to override these defaults.

The state file is temporary and contains the process id, port, project path,
protocol version, start time, and a random authentication token. It is deleted
when the bridge exits normally.

## JSONL protocol

The first line on every TCP connection must be:

```json
{"type":"bridge.connect","token":"<token from bridge.json>"}
```

After authentication, the bridge sends one `bridge.ready` line. Every later
line is forwarded unchanged to the Codex App Server stdin, and every non-empty
line from App Server stdout is forwarded unchanged to the authenticated Unity
client. This lets the existing JSON-RPC client continue to use its current
protocol and keeps scene control in the installed Unity Tool.

When the client is briefly disconnected, the bridge keeps the most recent 512
App Server lines in memory and sends them to the next authenticated connection
before forwarding new output. Outstanding App Server server requests, including
approval requests, are retained until Unity sends the matching JSON-RPC response.
A process restart starts a new session and generates a new token.

Stop the process with `Ctrl+C` or a normal process termination signal. The
bridge closes App Server stdin, waits briefly for a graceful exit, then kills
the child process tree if needed and removes the state file.
