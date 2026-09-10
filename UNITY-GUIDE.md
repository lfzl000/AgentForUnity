# Unity Development Guidelines

This document defines the required tools, content-editing boundaries, and validation rules for agents performing Unity development.

## 1. Core Tools

- Interact with Unity through Unity CLI/Pipeline. Do not use Unity MCP.
- When interacting with Unity, use the `unity-pipeline` skill and follow its instructions for commands, parameters, status polling, and workflows.
- For `unity pipeline` and `unity command` calls that connect to localhost, request the approved local execution context on the first attempt. Do not first test the loopback service from a restricted sandbox.
- When multiple Unity Editor instances or development Players are available, explicitly select the target to avoid operating on the wrong instance.
- Use the current instance's `unity command` list as the source of truth. Do not assume that a Pipeline capability is available.

## 2. Operation Boundaries

### Direct Editing Is Allowed

- Agents may create or modify ordinary `.cs` source files directly.
- Agents may also edit plain-text files such as shaders, CSV, JSON, and Markdown directly.
- When changing multiple scripts or text files, batch the edits first, then perform the necessary Unity refresh or compilation once per file type to avoid repeated compilation and Domain Reloads.
- When Pipeline is unavailable, edit only the plain-text files listed above. Do not modify serialized assets, and clearly state when required validation could not be completed.

### Pipeline Is Required

- Use Pipeline to create or modify Unity-managed serialized content, including scenes, Prefabs, Materials, ScriptableObjects, AnimationClips, AnimatorControllers, and Timelines.
- Use Pipeline for importing external assets, configuring their import settings, and validating the import. For batch imports, source files may be copied to the destination directory first.
- Use Pipeline to move, rename, copy, or delete existing files and directories under `Assets/`. Do not perform these operations directly through filesystem tools.
- Use Pipeline for scene-object operations, including creating or deleting GameObjects and changing their hierarchy, Transforms, components, or serialized fields.
- Use Pipeline for Unity project settings, including Build Settings, Tags/Layers, Input, Quality, Graphics, and Player Settings.
- Create and persist project content such as scenes, Prefabs, Materials, and visual effects in the Editor, with the necessary tunable parameters. Do not replace asset authoring with temporary runtime-generated content.

### Direct Operations Are Prohibited

- Do not manually create, modify, or delete `.meta` files, and do not reuse their GUIDs for other assets.
- Do not manually edit Unity serialized asset files such as `.unity`, `.prefab`, `.mat`, `.asset`, `.controller`, or `.anim` files.
- Do not manually modify Unity-generated content or local state files such as `Library/`, `Temp/`, `Logs/`, `UserSettings/`, `*.csproj`, or `*.sln`.
- Do not rewrite binary assets at the byte level.

## 3. Operating Principles

- Batch similar queries and modifications, then validate them together instead of processing each item separately.
- Preserve asset compatibility and script bindings when changing serialized fields, component classes, or assembly structure.
- Obtain explicit authorization before performing broad reimports, switching build targets, building a Player, or rewriting assets in bulk.
- Prefer dedicated `unity command` operations. Use `eval` only when no suitable command exists, keep the code scoped to the minimum required by the task, and handle Undo, dirty state, and saving as needed.
- Before a batch import, check destination paths and overwrite behavior. After copying assets without `.meta` files, let Unity refresh and import them. When assets include `.meta` files, copy the assets and metadata together and check for GUID conflicts.

## 4. Validation and Completion

- Perform validation appropriate to the changes. Do not run the full test suite unless necessary.
- Capture visual changes in batches, using staged validation when needed and avoiding repeated screenshots that provide no new information.
- Distinguish new Console messages from historical logs to avoid attributing unrelated errors to the current work.
- After scene or Prefab editing, or after Play Mode operations, save meaningful changes and discard temporary validation changes. Do not save or discard the user's existing unsaved work without authorization.
