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
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        private static void Shutdown()
        {
            EditorApplication.update -= Service.Update;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            Service.Dispose();
        }
    }
}
