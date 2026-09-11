using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine.Networking;

namespace AgentForUnity.Editor.Application
{
    internal sealed partial class AgentForUnityService
    {
        private const string PackageManifestPath = "Packages/com.zlr.agentforunity/package.json";
        private const string RemotePackageManifestUrl =
            "https://raw.githubusercontent.com/lfzl000/AgentForUnity/main/package.json";

        private CancellationTokenSource _packageUpdateCancellation;
        private bool _packageUpdateReloadLocked;

        internal bool PackageUpdateChecking { get; private set; }
        internal bool PackageUpdating { get; private set; }
        internal bool PackageUpdateAvailable { get; private set; }
        internal string AvailablePackageVersion { get; private set; } = string.Empty;
        internal string PackageUpdateStatus { get; private set; } = string.Empty;
        internal bool PackageUpdateFailed { get; private set; }
        internal bool CanUpdatePackage => PackageUpdateAvailable &&
                                          _packageUpdateCancellation == null &&
                                          !_disposed && !IsTurnActive && !GitBusy && !UnityToolingBusy &&
                                          !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                                          !EditorApplication.isPlayingOrWillChangePlaymode;

        internal async void CheckForPackageUpdate()
        {
            if (_disposed || _packageUpdateCancellation != null) return;

            var cancellation = new CancellationTokenSource();
            _packageUpdateCancellation = cancellation;
            PackageUpdateChecking = true;
            PackageUpdateAvailable = false;
            AvailablePackageVersion = string.Empty;
            PackageUpdateFailed = false;
            PackageUpdateStatus = "Checking for updates...";
            MarkChanged();
            try
            {
                var installedVersion = GetInstalledPackageVersion();
                var manifest = await DownloadTextAsync(RemotePackageManifestUrl, cancellation.Token);
                var remoteVersion = JObject.Parse(manifest).Value<string>("version");
                if (!Version.TryParse(installedVersion, out var installed) ||
                    !Version.TryParse(remoteVersion, out var remote))
                    throw new InvalidOperationException("Could not read a valid package version.");

                if (remote.CompareTo(installed) > 0)
                {
                    PackageUpdateAvailable = true;
                    AvailablePackageVersion = remoteVersion;
                    PackageUpdateStatus = "Update available: " + remoteVersion;
                }
                else
                {
                    PackageUpdateStatus = "Up to date (" + installedVersion + ")";
                }
            }
            catch (Exception exception)
            {
                if (!_disposed && !(exception is OperationCanceledException))
                {
                    PackageUpdateFailed = true;
                    PackageUpdateStatus = "Update check failed";
                    AddDiagnostic("Package update: " + exception.Message);
                }
            }
            finally
            {
                PackageUpdateChecking = false;
                if (ReferenceEquals(_packageUpdateCancellation, cancellation)) _packageUpdateCancellation = null;
                cancellation.Dispose();
                if (!_disposed) MarkChanged();
            }
        }

        internal async void UpdatePackage()
        {
            if (!CanUpdatePackage) return;

            var cancellation = new CancellationTokenSource();
            _packageUpdateCancellation = cancellation;
            PackageUpdating = true;
            EditorApplication.LockReloadAssemblies();
            _packageUpdateReloadLocked = true;
            PackageUpdateFailed = false;
            PackageUpdateStatus = "Updating to " + AvailablePackageVersion + "...";
            MarkChanged();
            try
            {
                var packagePath = GetInstalledPackagePath();
                if (string.IsNullOrEmpty(packagePath))
                    throw new InvalidOperationException("This package does not expose a local path.");

                var git = new AgentGitOperations();
                await git.OpenAsync(packagePath, cancellation.Token);
                await git.PullAsync(cancellation.Token);
                PackageUpdateAvailable = false;
                PackageUpdateStatus = "Updated to " + AvailablePackageVersion + ". Reloading Unity...";
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                if (!_disposed && !(exception is OperationCanceledException))
                {
                    PackageUpdateFailed = true;
                    PackageUpdateStatus = "Update failed";
                    AddDiagnostic("Package update: " + exception.Message);
                }
            }
            finally
            {
                if (ReferenceEquals(_packageUpdateCancellation, cancellation)) _packageUpdateCancellation = null;
                PackageUpdating = false;
                cancellation.Dispose();
                if (_packageUpdateReloadLocked)
                {
                    EditorApplication.UnlockReloadAssemblies();
                    _packageUpdateReloadLocked = false;
                }
                if (!_disposed) MarkChanged();
            }
        }

        private static string GetInstalledPackageVersion()
        {
            var packagePath = GetInstalledPackagePath();
            if (string.IsNullOrEmpty(packagePath))
                throw new InvalidOperationException("Could not find the installed Agent for Unity package.");
            return JObject.Parse(File.ReadAllText(Path.Combine(packagePath, "package.json"))).Value<string>("version");
        }

        private static string GetInstalledPackagePath()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageManifestPath);
            return package == null || string.IsNullOrEmpty(package.resolvedPath)
                ? null
                : Path.GetFullPath(package.resolvedPath);
        }

        private static Task<string> DownloadTextAsync(string url, CancellationToken token)
        {
            var completion = new TaskCompletionSource<string>();
            var request = UnityWebRequest.Get(url);
            var registration = token.Register(request.Abort);
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                registration.Dispose();
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (request.result != UnityWebRequest.Result.Success)
                        throw new InvalidOperationException(request.error ?? "The update server did not respond.");
                    completion.TrySetResult(request.downloadHandler.text);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    request.Dispose();
                }
            };
            return completion.Task;
        }

        private void DisposePackageUpdate()
        {
            _packageUpdateCancellation?.Cancel();
            _packageUpdateCancellation?.Dispose();
            _packageUpdateCancellation = null;
            PackageUpdating = false;
            if (_packageUpdateReloadLocked)
            {
                EditorApplication.UnlockReloadAssemblies();
                _packageUpdateReloadLocked = false;
            }
        }
    }
}
