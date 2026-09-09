using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentForUnity.Editor.Application
{
    [InitializeOnLoad]
    internal static class AgentForUnityCompilationMonitor
    {
        private const int MaxMessages = 200;
        private static readonly List<CompilerMessage> Messages = new List<CompilerMessage>();

        static AgentForUnityCompilationMonitor()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationStarted(object context)
        {
            Messages.Clear();
            AgentForUnityService.Instance.HandleCompilationStarted();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] compilerMessages)
        {
            if (compilerMessages == null || compilerMessages.Length == 0)
            {
                return;
            }

            Messages.AddRange(compilerMessages.Take(Math.Max(0, MaxMessages - Messages.Count)));
        }

        private static void OnCompilationFinished(object context)
        {
            var errors = Messages.Count(message => message.type == CompilerMessageType.Error);
            var warnings = Messages.Count(message => message.type == CompilerMessageType.Warning);
            var details = new StringBuilder();
            foreach (var message in Messages)
            {
                details.Append(message.type)
                    .Append(": ")
                    .Append(message.file)
                    .Append('(').Append(message.line).Append(',').Append(message.column).Append("): ")
                    .AppendLine(message.message);
            }

            AgentForUnityService.Instance.HandleCompilationFinished(errors, warnings, details.ToString().TrimEnd());
        }
    }
}
