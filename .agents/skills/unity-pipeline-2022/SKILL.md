---
name: unity-pipeline-2022
description: Adapt an embedded com.unity.pipeline 0.6.0-exp.1 package for Unity 2022.3 after Unity CLI installation. Use only when a Unity 2022 project needs Pipeline source compatibility; do not apply these patches to Unity 6 projects.
---

# Unity Pipeline 2022 Compatibility

Apply this workflow only to `Packages/com.unity.pipeline` in a Unity 2022.3 project. The package must be the embedded `com.unity.pipeline@0.6.0-exp.1` source distribution. Stop if the package name or version differs; upstream source changes require a fresh compatibility review.

## Required Changes

1. Change the package manifest's `unity` field from `6000.0` to `2022.3`.
2. In `Editor/Commands/Assets/AssetCommands.cs`, alias `PhysicsMaterialCompat` to `UnityEngine.PhysicsMaterial` on Unity 6 and `UnityEngine.PhysicMaterial` otherwise. Use the alias for all physics-material type checks and construction.
3. In `Editor/Commands/Materials/MaterialCommands.cs`, retain `Material.rawRenderQueue` on Unity 6. On Unity 2022, read `m_CustomRenderQueue` through `SerializedObject`, falling back to `material.renderQueue` when the serialized property is unavailable.
4. Compile the full `PipelineAnalytics` implementation only under `UNITY_6000_0_OR_NEWER`. Provide Unity 2022 no-op implementations of `RecordCommandExecuted(in CommandExecutionInfo)` and `SendSessionStoppedIfStarted()`.
5. Make `Unity.Pipeline`, `Unity.Pipeline.IlInterpreter`, `Unity.Pipeline.Editor`, and `Unity.Pipeline.CodeGen` Editor-only. In `Unity.Pipeline.Editor.asmdef`, remove `Unity.Nuget.Newtonsoft-Json` from assembly references and add `Newtonsoft.Json.dll` to precompiled references.
6. Keep Pipeline test assemblies out of Unity 2022 compilation by adding `UNITY_6000_0_OR_NEWER` to their define constraints. Use `optionalUnityReferences: ["TestAssemblies"]`, add `Newtonsoft.Json.dll`, and remove direct `UnityEditor.TestRunner` and `UnityEngine.TestRunner` references.
7. For the five DLLs under `Runtime/Plugins/CodeAnalysis`, use Unity 2022-compatible `PluginImporter` metadata with `serializedVersion: 2`, disable `Any`, and enable only `Editor`:
   - `Microsoft.CodeAnalysis.CSharp.dll`
   - `Microsoft.CodeAnalysis.dll`
   - `System.Collections.Immutable.dll`
   - `System.Reflection.Metadata.dll`
   - `System.Runtime.CompilerServices.Unsafe.dll`
8. In `Runtime/Common/BasePipelineServer.cs`, make each healthy watchdog tick call `UpdateHeartBeat()` when the server writes descriptors, and republish the descriptor after the watchdog repairs a dead listener. This keeps `Library/Pipeline/.unity-pipeline-port` fresh and recreates it if an external cleanup removes it.

Use JSON parsing for `package.json` and `.asmdef` files. Preserve every existing `.meta` GUID when rewriting importer settings. Make each transformation idempotent so a reload or retry does not duplicate aliases, guards, references, or define constraints.

## Validation Boundary

Confirm the package identity and inspect the changed source, JSON, and importer metadata. Do not run EditMode tests for this adaptation. Unity compilation and visual verification remain the user's responsibility.
