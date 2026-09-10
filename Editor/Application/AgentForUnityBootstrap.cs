using UnityEditor;

namespace AgentForUnity.Editor.Application
{
    [InitializeOnLoad]
    internal static class AgentForUnityBootstrap
    {
        internal static readonly AgentForUnityService Service;

        static AgentForUnityBootstrap()
        {
            Service = new AgentForUnityService();
            EditorApplication.update += Service.Update;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode ||
                !Service.IsTurnStarting ||
                !AgentForUnityService.EnterPlayModeReloadsDomain())
            {
                return;
            }

            EditorApplication.isPlaying = false;
            Service.HandlePlayModeEntryBlocked();
        }

        private static void Shutdown()
        {
            EditorApplication.update -= Service.Update;
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            Service.Dispose();
        }
    }
}
