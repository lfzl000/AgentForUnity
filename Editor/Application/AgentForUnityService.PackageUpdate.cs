using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
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
        internal string PackageUpdateError { get; private set; } = string.Empty;
        internal bool PackageUpdateFailed { get; private set; }
        internal bool CanUpdatePackage => PackageUpdateAvailable &&
                                          _packageUpdateCancellation == null &&
                                          !_disposed && !IsTurnActive && !GitBusy &&
                                          !(ToolingSetupPending && !ToolingSetupFailed) &&
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
            PackageUpdateError = string.Empty;
            PackageUpdateStatus = "Checking for updates...";
            MarkChanged();
            try
            {
                var installedVersion = GetInstalledPackageVersion();
                var manifest = await DownloadTextAsync(
                    RemotePackageManifestUrl + "?cachebust=" + DateTime.UtcNow.Ticks,
                    cancellation.Token);
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
                    PackageUpdateError = exception.Message;
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
            PackageUpdateError = string.Empty;
            PackageUpdateStatus = "Updating to " + AvailablePackageVersion + "...";
            MarkChanged();
            try
            {
                var package = GetInstalledPackageInfo();
                var packagePath = package?.resolvedPath;
                if (string.IsNullOrEmpty(packagePath))
                    throw new InvalidOperationException("This package does not expose a local path.");

                if (package.source == UnityEditor.PackageManager.PackageSource.Git)
                {
                    // Git packages live in Library/PackageCache, not a writable Git checkout.
                    // Explicitly adding the manifest's Git reference makes UPM refresh its locked revision.
                    var packageReference = GetPackageDependencyReference();
                    PackageUpdateStatus = "Resolving Git package...";
                    MarkChanged();
                    var request = UnityEditor.PackageManager.Client.Add(packageReference);
                    await WaitForPackageManagerRequestAsync(request, cancellation.Token);
                    PackageUpdateAvailable = false;
                    PackageUpdateStatus = "Updated to " + AvailablePackageVersion + ". Reloading Unity...";
                    AssetDatabase.Refresh();
                    return;
                }

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
                    PackageUpdateError = exception.Message;
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
            var package = GetInstalledPackageInfo();
            return package == null || string.IsNullOrEmpty(package.resolvedPath)
                ? null
                : Path.GetFullPath(package.resolvedPath);
        }

        private static UnityEditor.PackageManager.PackageInfo GetInstalledPackageInfo()
        {
            return UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageManifestPath);
        }

        private string GetPackageDependencyReference()
        {
            var manifestPath = Path.Combine(_projectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException("Could not find the Unity package manifest.");

            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var packageReference = (manifest["dependencies"] as JObject)?.Value<string>("com.zlr.agentforunity");
            if (string.IsNullOrWhiteSpace(packageReference) ||
                !packageReference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Agent for Unity is not installed from a supported Git URL.");
            return packageReference;
        }

        private static Task WaitForPackageManagerRequestAsync(AddRequest request, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>();
            CancellationTokenRegistration registration = default;
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (completion.Task.IsCompleted)
                {
                    EditorApplication.update -= poll;
                    registration.Dispose();
                    return;
                }

                if (!request.IsCompleted) return;
                EditorApplication.update -= poll;
                registration.Dispose();
                if (request.Status == StatusCode.Failure)
                {
                    completion.TrySetException(new InvalidOperationException(
                        request.Error?.message ?? "Unity Package Manager could not update the Git package."));
                }
                else
                {
                    completion.TrySetResult(true);
                }
            };
            registration = token.Register(() => completion.TrySetCanceled());
            EditorApplication.update += poll;
            poll();
            return completion.Task;
        }

        private static Task<string> DownloadTextAsync(string url, CancellationToken token)
        {
            var completion = new TaskCompletionSource<string>();
            var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Cache-Control", "no-cache");
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
