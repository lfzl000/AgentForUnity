using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentForUnity.Editor.Application
{
    internal sealed class UnityCliInstallation
    {
        internal UnityCliInstallation(string path, string version, string error)
        {
            Path = path;
            Version = version;
            Error = error;
        }

        internal string Path { get; }
        internal string Version { get; }
        internal string Error { get; }
        internal bool IsAvailable => !string.IsNullOrEmpty(Path) && string.IsNullOrEmpty(Error);
    }

    internal sealed class PipelineServerConnection
    {
        internal PipelineServerConnection(bool isReachable, string endpoint, string status)
        {
            IsReachable = isReachable;
            Endpoint = endpoint;
            Status = status;
        }

        internal bool IsReachable { get; }
        internal string Endpoint { get; }
        internal string Status { get; }
    }

    internal sealed partial class AgentForUnityService
    {
        private const double UnityToolingRefreshSeconds = 2d;
        private const double PipelineServerRefreshSeconds = 10d;

        private bool _unityToolingBusy;
        private bool _unityCliInstalled;
        private bool _pipelineInstalled;
        private bool _pipelineUnityVersionSupported;
        private bool _pipelineRequiresUnity2022Adaptation;
        private bool _pipelineProjectSetupComplete;
        private bool _pipelineServerChecking;
        private bool _pipelineServerReachable;
        private int _unityToolingOperation;
        private double _nextUnityToolingRefreshTime;
        private double _nextPipelineServerRefreshTime;
        private string _unityCliPath;
        private string _unityCliVersion;
        private string _unityCliStatus = "Not checked";
        private string _pipelineVersion;
        private string _pipelineStatus = "Not checked";
        private string _pipelineServerEndpoint;
        private string _pipelineServerStatus = "Not checked";

        internal bool UnityToolingBusy => _unityToolingBusy;
        internal bool UnityCliInstalled => _unityCliInstalled;
        internal bool PipelineInstalled => _pipelineInstalled;
        internal bool PipelineUnityVersionSupported => _pipelineUnityVersionSupported;
        internal bool PipelineRequiresUnity2022Adaptation => _pipelineRequiresUnity2022Adaptation;
        internal bool PipelineProjectSetupComplete => _pipelineProjectSetupComplete;
        internal bool PipelineServerChecking => _pipelineServerChecking;
        internal bool PipelineServerReachable => _pipelineServerReachable;
        internal string UnityCliStatus => _unityCliStatus;
        internal string PipelineStatus => _pipelineStatus;
        internal string PipelineServerStatus => _pipelineServerStatus;
        internal string PipelineServerEndpoint => _pipelineServerEndpoint;
        internal string UnityCliToolPath => _unityCliPath;
        internal string UnityCliToolVersion => _unityCliVersion;
        internal string PipelineVersion => _pipelineVersion;
        internal bool CanInstallUnityCli => !_disposed && !_unityToolingBusy && !_unityCliInstalled;
        internal bool CanInstallPipeline => !_disposed && !_unityToolingBusy && _pipelineUnityVersionSupported &&
                                            (!_pipelineInstalled || !_pipelineProjectSetupComplete);

        internal async void RefreshUnityTooling()
        {
            if (_disposed || _unityToolingBusy)
            {
                return;
            }

            var operation = BeginUnityToolingOperation("Detecting Unity CLI", "Checking Pipeline package");
            try
            {
                var cli = await UnityToolingInstaller.DetectUnityCliAsync();
                if (!IsCurrentUnityToolingOperation(operation))
                {
                    return;
                }

                ApplyUnityCliDetection(cli);
                RefreshPipelineInstallation();
                if (_pipelineInstalled &&
                    _pipelineUnityVersionSupported &&
                    !_pipelineRequiresUnity2022Adaptation &&
                    File.Exists(UnityToolingInstaller.PendingSetupPath(_projectRoot)))
                {
                    CompletePipelineProjectSetup();
                }

                await RefreshPipelineServerStatusAsync(operation);
            }
            catch (Exception exception)
            {
                if (IsCurrentUnityToolingOperation(operation))
                {
                    if (!_unityCliInstalled)
                    {
                        _unityCliStatus = "Detection failed";
                    }
                    _pipelineStatus = _pipelineInstalled ? "Project setup failed" : "Detection failed";
                    AddDiagnostic("Unity tooling detection failed: " + exception.Message);
                }
            }
            finally
            {
                EndUnityToolingOperation(operation);
            }
        }

        internal async void InstallUnityCli()
        {
            if (!CanInstallUnityCli)
            {
                return;
            }

            var operation = BeginUnityToolingOperation("Installing Unity CLI", _pipelineStatus);
            try
            {
                var cli = await UnityToolingInstaller.EnsureUnityCliInstalledAsync();
                if (!IsCurrentUnityToolingOperation(operation))
                {
                    return;
                }

                ApplyUnityCliDetection(cli);
                if (!cli.IsAvailable)
                {
                    throw new InvalidOperationException(cli.Error ?? "Unity CLI installation failed.");
                }
            }
            catch (Exception exception)
            {
                if (IsCurrentUnityToolingOperation(operation))
                {
                    _unityCliInstalled = false;
                    _unityCliStatus = "Installation failed";
                    AddDiagnostic("Unity CLI installation failed: " + exception.Message);
                }
            }
            finally
            {
                EndUnityToolingOperation(operation);
            }
        }

        internal async void InstallPipeline()
        {
            if (!CanInstallPipeline)
            {
                return;
            }

            var operation = BeginUnityToolingOperation(_unityCliStatus, "Preparing Pipeline installation");
            try
            {
                var cli = await UnityToolingInstaller.EnsureUnityCliInstalledAsync();
                if (!IsCurrentUnityToolingOperation(operation))
                {
                    return;
                }

                ApplyUnityCliDetection(cli);
                if (!cli.IsAvailable)
                {
                    throw new InvalidOperationException(cli.Error ?? "Unity CLI installation failed.");
                }

                RefreshPipelineInstallation();
                if (_pipelineInstalled && !_pipelineRequiresUnity2022Adaptation)
                {
                    _pipelineStatus = "Finishing project setup";
                    MarkChanged();
                    CompletePipelineProjectSetup();
                    return;
                }

                UnityToolingInstaller.MarkSetupPending(_projectRoot);
                _pipelineStatus = UnityEngine.Application.unityVersion.StartsWith("2022.", StringComparison.Ordinal)
                    ? "Installing Unity 2022 compatible source"
                    : "Installing with Unity CLI";
                MarkChanged();

                if (UnityEngine.Application.unityVersion.StartsWith("2022.", StringComparison.Ordinal))
                {
                    await UnityToolingInstaller.InstallUnity2022PipelineAsync(_projectRoot);
                }
                else
                {
                    await UnityToolingInstaller.InstallPipelineWithCliAsync(cli.Path, _projectRoot);
                }

                if (!IsCurrentUnityToolingOperation(operation))
                {
                    return;
                }

                AssetDatabase.Refresh();
                RefreshPipelineInstallation();
                if (_pipelineInstalled)
                {
                    CompletePipelineProjectSetup();
                }
                else
                {
                    _pipelineStatus = "Waiting for Unity to resolve the package";
                }
            }
            catch (Exception exception)
            {
                if (IsCurrentUnityToolingOperation(operation))
                {
                    _pipelineStatus = "Installation failed";
                    UnityToolingInstaller.ClearSetupPending(_projectRoot);
                    AddDiagnostic("Pipeline installation failed: " + exception.Message);
                }
            }
            finally
            {
                EndUnityToolingOperation(operation);
            }
        }

        internal void ResumePendingUnityToolingSetup()
        {
            if (_disposed || _unityToolingBusy || !File.Exists(UnityToolingInstaller.PendingSetupPath(_projectRoot)))
            {
                return;
            }

            RefreshPipelineInstallation();
            if (!_pipelineInstalled || !_pipelineUnityVersionSupported)
            {
                return;
            }
            if (_pipelineRequiresUnity2022Adaptation)
            {
                InstallPipeline();
                return;
            }

            try
            {
                CompletePipelineProjectSetup();
            }
            catch (Exception exception)
            {
                _pipelineStatus = "Project setup failed";
                AddDiagnostic("Pipeline project setup failed: " + exception.Message);
                MarkChanged();
            }
        }

        private void UpdateUnityToolingSetup()
        {
            if (_disposed)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (!_unityToolingBusy && now >= _nextUnityToolingRefreshTime)
            {
                _nextUnityToolingRefreshTime = now + UnityToolingRefreshSeconds;
                ResumePendingUnityToolingSetup();
            }

            if (!_unityToolingBusy && !_pipelineServerChecking && now >= _nextPipelineServerRefreshTime)
            {
                _nextPipelineServerRefreshTime = now + PipelineServerRefreshSeconds;
                RefreshPipelineServerStatus();
            }
        }

        private async void RefreshPipelineServerStatus()
        {
            await RefreshPipelineServerStatusAsync();
        }

        private async Task RefreshPipelineServerStatusAsync(int toolingOperation = 0)
        {
            if (_disposed || _pipelineServerChecking)
            {
                return;
            }

            _pipelineServerChecking = true;
            _pipelineServerStatus = "Checking connection";
            MarkChanged();
            try
            {
                var connection = await UnityToolingInstaller.DetectPipelineServerAsync(_projectRoot);
                if (_disposed || toolingOperation != 0 && !IsCurrentUnityToolingOperation(toolingOperation))
                {
                    return;
                }

                _pipelineServerReachable = connection.IsReachable;
                _pipelineServerEndpoint = connection.Endpoint;
                _pipelineServerStatus = connection.Status;
            }
            catch (Exception exception)
            {
                if (!_disposed && (toolingOperation == 0 || IsCurrentUnityToolingOperation(toolingOperation)))
                {
                    _pipelineServerReachable = false;
                    _pipelineServerEndpoint = null;
                    _pipelineServerStatus = "Detection failed · " + exception.Message;
                }
            }
            finally
            {
                _pipelineServerChecking = false;
                if (!_disposed)
                {
                    MarkChanged();
                }
            }
        }

        private int BeginUnityToolingOperation(string cliStatus, string pipelineStatus)
        {
            _unityToolingBusy = true;
            var operation = ++_unityToolingOperation;
            _unityCliStatus = cliStatus;
            _pipelineStatus = pipelineStatus;
            MarkChanged();
            return operation;
        }

        private bool IsCurrentUnityToolingOperation(int operation)
        {
            return !_disposed && operation == _unityToolingOperation;
        }

        private void EndUnityToolingOperation(int operation)
        {
            if (operation != _unityToolingOperation)
            {
                return;
            }

            _unityToolingBusy = false;
            MarkChanged();
        }

        private void ApplyUnityCliDetection(UnityCliInstallation cli)
        {
            _unityCliInstalled = cli.IsAvailable;
            _unityCliPath = cli.Path;
            _unityCliVersion = cli.Version;
            _unityCliStatus = cli.IsAvailable
                ? "Installed" + (string.IsNullOrEmpty(cli.Version) ? string.Empty : " · " + cli.Version)
                : "Not installed";
            if (!cli.IsAvailable && !string.IsNullOrWhiteSpace(cli.Error))
            {
                _unityCliStatus += " · " + cli.Error;
            }
        }

        private void RefreshPipelineInstallation()
        {
            var pipeline = UnityToolingInstaller.FindPipelinePackage(_projectRoot);
            _pipelineInstalled = pipeline != null;
            _pipelineVersion = pipeline?.Version;
            _pipelineUnityVersionSupported = UnityToolingInstaller.IsPipelineUnityVersionSupported(
                UnityEngine.Application.unityVersion);
            _pipelineRequiresUnity2022Adaptation = _pipelineInstalled &&
                                                    UnityEngine.Application.unityVersion.StartsWith("2022.", StringComparison.Ordinal) &&
                                                    !UnityToolingInstaller.IsUnity2022PipelineAdapted(_projectRoot);
            _pipelineProjectSetupComplete = _pipelineInstalled &&
                                            _pipelineUnityVersionSupported &&
                                            !_pipelineRequiresUnity2022Adaptation &&
                                            UnityToolingInstaller.IsProjectSetupComplete(_projectRoot);
            var versionSuffix = string.IsNullOrEmpty(_pipelineVersion) ? string.Empty : " · " + _pipelineVersion;
            if (!_pipelineUnityVersionSupported)
            {
                _pipelineStatus = _pipelineInstalled
                    ? "Installed" + versionSuffix + " · Unsupported Unity version"
                    : "Unsupported Unity version";
                return;
            }
            if (!_pipelineInstalled)
            {
                _pipelineStatus = "Not installed";
                return;
            }

            if (_pipelineRequiresUnity2022Adaptation)
            {
                _pipelineStatus = "Installed" + versionSuffix + " · Unity 2022 adaptation required";
                return;
            }

            _pipelineStatus = _pipelineProjectSetupComplete
                ? "Installed" + versionSuffix + " · Skills ready"
                : "Installed" + versionSuffix + " · Project setup incomplete";
        }

        private void CompletePipelineProjectSetup()
        {
            var pipeline = UnityToolingInstaller.FindPipelinePackage(_projectRoot);
            if (pipeline == null)
            {
                throw new InvalidOperationException("The Pipeline package is not available yet.");
            }
            if (!_pipelineUnityVersionSupported)
            {
                throw new InvalidOperationException("Pipeline setup supports Unity 2022 or Unity 6 and newer.");
            }
            if (_pipelineRequiresUnity2022Adaptation)
            {
                throw new InvalidOperationException("The Pipeline package must be adapted for Unity 2022 before project setup.");
            }

            UnityToolingInstaller.InstallProjectSupport(_projectRoot, pipeline.ResolvedPath);
            UnityToolingInstaller.ClearSetupPending(_projectRoot);
            RefreshPipelineInstallation();
            MarkChanged();
        }
    }

    internal sealed class PipelinePackageInstallation
    {
        internal PipelinePackageInstallation(string resolvedPath, string version)
        {
            ResolvedPath = resolvedPath;
            Version = version;
        }

        internal string ResolvedPath { get; }
        internal string Version { get; }
    }

    internal static class UnityToolingInstaller
    {
        private const string PipelinePackageName = "com.unity.pipeline";
        private const string Pipeline2022Version = "0.6.0-exp.1";
        private const long Pipeline2022ArchiveBytes = 5380590;
        private const string Pipeline2022Sha1 = "9bb4172c603cda2626bacac4d4c64bdb8270a3e0";
        private const string Pipeline2022Url =
            "https://download.packages.unity.com/com.unity.pipeline/-/com.unity.pipeline-0.6.0-exp.1.tgz";
        private const string PipelineLocalExecutionInstruction =
            "For every `unity pipeline` or `unity command` invocation that connects to an Editor or Player on localhost, request the approved local execution context on the first attempt. Do not probe the loopback endpoint from the restricted sandbox first. Use narrow reusable command-prefix approval rules such as `unity pipeline list` and `unity command` when the host supports them.";

        private static readonly string[] CodeAnalysisPluginNames =
        {
            "Microsoft.CodeAnalysis.CSharp.dll.meta",
            "Microsoft.CodeAnalysis.dll.meta",
            "System.Collections.Immutable.dll.meta",
            "System.Reflection.Metadata.dll.meta",
            "System.Runtime.CompilerServices.Unsafe.dll.meta"
        };

        internal static Task<UnityCliInstallation> DetectUnityCliAsync()
        {
            return Task.Run(DetectUnityCli);
        }

        internal static Task<PipelineServerConnection> DetectPipelineServerAsync(string projectRoot)
        {
            return Task.Run(() => DetectPipelineServer(projectRoot));
        }

        internal static bool IsPipelineUnityVersionSupported(string unityVersion)
        {
            if (string.IsNullOrWhiteSpace(unityVersion))
            {
                return false;
            }
            if (unityVersion.StartsWith("2022.", StringComparison.Ordinal))
            {
                return true;
            }

            var separator = unityVersion.IndexOf('.');
            var majorText = separator < 0 ? unityVersion : unityVersion.Substring(0, separator);
            return int.TryParse(majorText, out var major) && major >= 6000;
        }

        private static PipelineServerConnection DetectPipelineServer(string projectRoot)
        {
            var descriptorPath = Path.Combine(projectRoot, "Library", "Pipeline", ".unity-pipeline-port");
            if (!File.Exists(descriptorPath))
            {
                return new PipelineServerConnection(false, null, "Unavailable · Instance descriptor missing");
            }

            int port;
            string token;
            try
            {
                var descriptor = JObject.Parse(File.ReadAllText(descriptorPath));
                port = descriptor.Value<int?>("port") ?? 0;
                token = descriptor.Value<string>("evalToken");
            }
            catch (Exception)
            {
                return new PipelineServerConnection(false, null, "Unavailable · Invalid instance descriptor");
            }

            if (port <= 0 || port > ushort.MaxValue || string.IsNullOrWhiteSpace(token))
            {
                return new PipelineServerConnection(false, null, "Unavailable · Invalid instance descriptor");
            }

            var endpoint = "http://127.0.0.1:" + port + "/api/status";
            try
            {
                var request = WebRequest.CreateHttp(endpoint);
                request.Method = "GET";
                request.Proxy = null;
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return new PipelineServerConnection(
                            true,
                            endpoint,
                            "Reachable · 127.0.0.1:" + port);
                    }

                    return new PipelineServerConnection(
                        false,
                        endpoint,
                        "Unreachable · HTTP " + (int)response.StatusCode);
                }
            }
            catch (WebException exception)
            {
                using (var response = exception.Response as HttpWebResponse)
                {
                    if (response?.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        return new PipelineServerConnection(
                            false,
                            endpoint,
                            "Unreachable · Authentication failed");
                    }

                    if (response != null)
                    {
                        return new PipelineServerConnection(
                            false,
                            endpoint,
                            "Unreachable · HTTP " + (int)response.StatusCode);
                    }
                }

                return new PipelineServerConnection(false, endpoint, "Unreachable · " + exception.Status);
            }
            catch (Exception exception)
            {
                return new PipelineServerConnection(false, endpoint, "Unreachable · " + exception.Message);
            }
        }

        internal static async Task<UnityCliInstallation> EnsureUnityCliInstalledAsync()
        {
            var current = await DetectUnityCliAsync();
            if (current.IsAvailable)
            {
                return current;
            }

            var result = await RunUnityCliInstallerAsync();
            if (!result.Success)
            {
                return new UnityCliInstallation(null, null, result.Error);
            }

            return await DetectUnityCliAsync();
        }

        internal static async Task InstallPipelineWithCliAsync(string cliPath, string projectRoot)
        {
            var result = await RunProcessAsync(
                cliPath,
                "--non-interactive --no-banner pipeline install --project-path " + QuoteArgument(projectRoot),
                projectRoot,
                180000);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error);
            }
        }

        internal static async Task InstallUnity2022PipelineAsync(string projectRoot)
        {
            var packageRoot = Path.Combine(projectRoot, "Packages", PipelinePackageName);
            if (!Directory.Exists(packageRoot))
            {
                var stagingPath = packageRoot + ".agentforunity-installing";
                var temporaryRoot = Path.Combine(Path.GetTempPath(), "agentforunity-pipeline-" + Guid.NewGuid().ToString("N"));
                var archivePath = Path.Combine(temporaryRoot, PipelinePackageName + ".tgz");
                var extractedPath = Path.Combine(temporaryRoot, "package");
                Directory.CreateDirectory(extractedPath);
                try
                {
                    await DownloadPipelineArchiveAsync(archivePath);
                    await ExtractArchiveAsync(archivePath, extractedPath);
                    ValidatePipelinePackage(extractedPath);
                    TryDeleteDirectory(stagingPath);
                    CopyDirectory(extractedPath, stagingPath, false);
                    ValidatePipelinePackage(stagingPath);
                    Directory.Move(stagingPath, packageRoot);
                }
                finally
                {
                    TryDeleteDirectory(stagingPath);
                    TryDeleteDirectory(temporaryRoot);
                }
            }
            else
            {
                ValidatePipelinePackage(packageRoot);
            }

            PatchPipelineForUnity2022(packageRoot);
        }

        internal static bool IsUnity2022PipelineAdapted(string projectRoot)
        {
            var packageRoot = Path.Combine(projectRoot, "Packages", PipelinePackageName);
            var manifestPath = Path.Combine(packageRoot, "package.json");
            var assetsPath = Path.Combine(packageRoot, "Editor", "Commands", "Assets", "AssetCommands.cs");
            var materialsPath = Path.Combine(packageRoot, "Editor", "Commands", "Materials", "MaterialCommands.cs");
            var analyticsPath = Path.Combine(packageRoot, "Editor", "PipelineAnalytics.cs");
            var serverPath = Path.Combine(packageRoot, "Runtime", "Common", "BasePipelineServer.cs");
            if (!File.Exists(manifestPath) ||
                !File.Exists(assetsPath) ||
                !File.Exists(materialsPath) ||
                !File.Exists(analyticsPath) ||
                !File.Exists(serverPath))
            {
                return false;
            }

            try
            {
                var package = JObject.Parse(File.ReadAllText(manifestPath));
                if (!string.Equals(package.Value<string>("name"), PipelinePackageName, StringComparison.Ordinal) ||
                    !string.Equals(package.Value<string>("version"), Pipeline2022Version, StringComparison.Ordinal) ||
                    !string.Equals(package.Value<string>("unity"), "2022.3", StringComparison.Ordinal) ||
                    !File.ReadAllText(assetsPath).Contains("using PhysicsMaterialCompat") ||
                    !File.ReadAllText(materialsPath).Contains("GetRawRenderQueue(Material material)") ||
                    !IsUnity2022AnalyticsAdapted(File.ReadAllText(analyticsPath)) ||
                    !IsPipelineDescriptorSelfHealingAdapted(File.ReadAllText(serverPath)))
                {
                    return false;
                }

                if (!IsEditorOnlyAssembly(Path.Combine(packageRoot, "CodeGen", "Unity.Pipeline.CodeGen.asmdef")) ||
                    !IsEditorOnlyAssembly(Path.Combine(packageRoot, "Runtime", "Unity.Pipeline.asmdef")) ||
                    !IsEditorOnlyAssembly(Path.Combine(packageRoot, "Runtime", "IlInterpreter", "Unity.Pipeline.IlInterpreter.asmdef")) ||
                    !IsUnity2022EditorAssemblyAdapted(Path.Combine(packageRoot, "Editor", "Unity.Pipeline.Editor.asmdef")) ||
                    !IsUnity2022TestAssemblyAdapted(Path.Combine(packageRoot, "Tests", "Editor", "Unity.Pipeline.Tests.Editor.asmdef")) ||
                    !IsUnity2022TestAssemblyAdapted(Path.Combine(packageRoot, "Tests", "Runtime", "Unity.Pipeline.Tests.Runtime.asmdef")))
                {
                    return false;
                }

                var pluginsRoot = Path.Combine(packageRoot, "Runtime", "Plugins", "CodeAnalysis");
                return CodeAnalysisPluginNames.All(pluginName =>
                    IsUnity2022EditorOnlyPlugin(Path.Combine(pluginsRoot, pluginName)));
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static PipelinePackageInstallation FindPipelinePackage(string projectRoot)
        {
            var embeddedPath = Path.Combine(projectRoot, "Packages", PipelinePackageName);
            var embeddedManifest = Path.Combine(embeddedPath, "package.json");
            if (File.Exists(embeddedManifest))
            {
                return new PipelinePackageInstallation(embeddedPath, ReadPackageVersion(embeddedManifest));
            }

            try
            {
                var registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(package => string.Equals(package.name, PipelinePackageName, StringComparison.Ordinal));
                if (registered != null)
                {
                    return new PipelinePackageInstallation(registered.resolvedPath, registered.version);
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        internal static void InstallProjectSupport(string projectRoot, string pipelineRoot)
        {
            var projectSkillsRoot = Path.Combine(projectRoot, ".agents", "skills");
            var pipelineSkillSource = Path.Combine(pipelineRoot, ".claude", "skills", "unity-pipeline");
            var pipelineSkillTarget = Path.Combine(projectSkillsRoot, "unity-pipeline");
            if (!File.Exists(Path.Combine(pipelineSkillSource, "SKILL.md")))
            {
                throw new FileNotFoundException("The Pipeline package does not contain its unity-pipeline skill.", pipelineSkillSource);
            }

            CopyDirectory(pipelineSkillSource, pipelineSkillTarget, true);
            EnsurePipelineLocalExecutionInstruction(Path.Combine(pipelineSkillTarget, "SKILL.md"));

            if (UnityEngine.Application.unityVersion.StartsWith("2022.", StringComparison.Ordinal))
            {
                var packageRoot = ResolveAgentForUnityPackageRoot();
                var compatibilitySkillSource = Path.Combine(packageRoot, ".agents", "skills", "unity-pipeline-2022");
                var compatibilitySkillTarget = Path.Combine(projectSkillsRoot, "unity-pipeline-2022");
                CopyDirectory(compatibilitySkillSource, compatibilitySkillTarget, true);
            }

            var agentPackageRoot = ResolveAgentForUnityPackageRoot();
            var guideSource = Path.Combine(agentPackageRoot, "UNITY-GUIDE.md");
            var guideTarget = Path.Combine(projectRoot, "UNITY-GUIDE.md");
            if (!File.Exists(guideTarget))
            {
                File.Copy(guideSource, guideTarget);
            }

            EnsureAgentsInstruction(Path.Combine(projectRoot, "AGENTS.md"));
        }

        internal static bool IsProjectSetupComplete(string projectRoot)
        {
            var skillsRoot = Path.Combine(projectRoot, ".agents", "skills");
            var pipelineSkillPath = Path.Combine(skillsRoot, "unity-pipeline", "SKILL.md");
            if (!File.Exists(pipelineSkillPath) ||
                !PipelineLocalExecutionInstructionExists(pipelineSkillPath) ||
                !File.Exists(Path.Combine(projectRoot, "UNITY-GUIDE.md")) ||
                !AgentsInstructionExists(Path.Combine(projectRoot, "AGENTS.md")))
            {
                return false;
            }

            return !UnityEngine.Application.unityVersion.StartsWith("2022.", StringComparison.Ordinal) ||
                   File.Exists(Path.Combine(skillsRoot, "unity-pipeline-2022", "SKILL.md"));
        }

        internal static string PendingSetupPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "AgentForUnity", "pipeline-setup.pending");
        }

        internal static void MarkSetupPending(string projectRoot)
        {
            var path = PendingSetupPath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? projectRoot);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"), new UTF8Encoding(false));
        }

        internal static void ClearSetupPending(string projectRoot)
        {
            var path = PendingSetupPath(projectRoot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static UnityCliInstallation DetectUnityCli()
        {
            var executableName = Environment.OSVersion.Platform == PlatformID.Win32NT ? "unity.exe" : "unity";
            var candidates = new List<string>();
            var configuredPath = Environment.GetEnvironmentVariable("UNITY_CLI_EXECUTABLE");
            AddCandidate(candidates, configuredPath);

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddCandidate(candidates, Path.Combine(directory.Trim(), executableName));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddCandidate(candidates, Path.Combine(userProfile, ".unity", "bin", executableName));
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                AddCandidate(candidates, "/opt/homebrew/bin/unity");
                AddCandidate(candidates, "/usr/local/bin/unity");
            }

            string firstError = null;
            foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var result = RunProcess(candidate, "--version", null, 10000);
                if (result.Success)
                {
                    return new UnityCliInstallation(candidate, FirstNonEmptyLine(result.Output), null);
                }

                if (firstError == null)
                {
                    firstError = result.Error;
                }
            }

            return new UnityCliInstallation(null, null, firstError ?? "Unity CLI was not found.");
        }

        private static Task<ProcessResult> RunUnityCliInstallerAsync()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                const string command =
                    "$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex";
                return RunProcessAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(command), null, 180000);
            }

            const string unixCommand =
                "curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash";
            return RunProcessAsync("/bin/bash", "-lc " + QuoteArgument(unixCommand), null, 180000);
        }

        private static async Task DownloadPipelineArchiveAsync(string archivePath)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                const long chunkSize = 1024 * 1024;
                for (long start = 0; start < Pipeline2022ArchiveBytes; start += chunkSize)
                {
                    var end = Math.Min(start + chunkSize - 1, Pipeline2022ArchiveBytes - 1);
                    await DownloadRangeAsync(start, end, destination);
                }
            }

            var actualLength = new FileInfo(archivePath).Length;
            if (actualLength != Pipeline2022ArchiveBytes)
            {
                throw new InvalidDataException(
                    $"Pipeline archive length mismatch. Expected {Pipeline2022ArchiveBytes}, received {actualLength}.");
            }

            using (var sha1 = SHA1.Create())
            using (var input = File.OpenRead(archivePath))
            {
                var hash = BitConverter.ToString(sha1.ComputeHash(input)).Replace("-", string.Empty).ToLowerInvariant();
                if (!string.Equals(hash, Pipeline2022Sha1, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Pipeline archive checksum mismatch.");
                }
            }

            using (var input = File.OpenRead(archivePath))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[8192];
                while (gzip.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }
        }

        private static async Task DownloadRangeAsync(long start, long end, Stream destination)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var request = WebRequest.CreateHttp(Pipeline2022Url);
                    request.Method = "GET";
                    request.AddRange(start, end);
                    request.Timeout = 60000;
                    request.ReadWriteTimeout = 60000;
                    using (var response = (HttpWebResponse)await request.GetResponseAsync())
                    using (var input = response.GetResponseStream())
                    using (var range = new MemoryStream())
                    {
                        if (response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            throw new InvalidDataException("The Pipeline CDN did not honor the requested byte range.");
                        }

                        var expected = end - start + 1;
                        var copied = await CopyExactlyAsync(input, range, expected);
                        if (copied != expected)
                        {
                            throw new EndOfStreamException($"Pipeline range {start}-{end} was truncated.");
                        }

                        range.Position = 0;
                        await range.CopyToAsync(destination);
                    }

                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }

            throw new InvalidOperationException($"Could not download Pipeline bytes {start}-{end}.", lastError);
        }

        private static async Task<long> CopyExactlyAsync(Stream input, Stream output, long expected)
        {
            var buffer = new byte[81920];
            long copied = 0;
            while (copied < expected)
            {
                var read = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, expected - copied));
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer, 0, read);
                copied += read;
            }

            return copied;
        }

        private static async Task ExtractArchiveAsync(string archivePath, string destination)
        {
            var executable = Environment.OSVersion.Platform == PlatformID.Win32NT ? "tar.exe" : "/usr/bin/tar";
            var result = await RunProcessAsync(
                executable,
                "-xzf " + QuoteArgument(archivePath) + " --strip-components=1 -C " + QuoteArgument(destination),
                null,
                60000);
            if (!result.Success)
            {
                throw new InvalidOperationException("Could not extract the Pipeline package: " + result.Error);
            }
        }

        private static void PatchPipelineForUnity2022(string packageRoot)
        {
            PatchPackageManifest(packageRoot);
            PatchPhysicsMaterial(Path.Combine(packageRoot, "Editor", "Commands", "Assets", "AssetCommands.cs"));
            PatchMaterialRenderQueue(Path.Combine(packageRoot, "Editor", "Commands", "Materials", "MaterialCommands.cs"));
            PatchAnalytics(Path.Combine(packageRoot, "Editor", "PipelineAnalytics.cs"));
            PatchPipelineDescriptorSelfHealing(Path.Combine(packageRoot, "Runtime", "Common", "BasePipelineServer.cs"));
            PatchAssemblyDefinitions(packageRoot);
            PatchCodeAnalysisPlugins(packageRoot);
        }

        private static void PatchPackageManifest(string packageRoot)
        {
            var path = Path.Combine(packageRoot, "package.json");
            var package = JObject.Parse(File.ReadAllText(path));
            package["unity"] = "2022.3";
            WriteText(path, package.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static void PatchPhysicsMaterial(string path)
        {
            var text = File.ReadAllText(path);
            const string alias =
                "#if UNITY_6000_0_OR_NEWER\nusing PhysicsMaterialCompat = UnityEngine.PhysicsMaterial;\n#else\nusing PhysicsMaterialCompat = UnityEngine.PhysicMaterial;\n#endif\n";
            if (!text.Contains("using PhysicsMaterialCompat"))
            {
                text = ReplaceRequired(
                    text,
                    "using Object = UnityEngine.Object;\n",
                    "using Object = UnityEngine.Object;\n" + alias,
                    "PhysicsMaterial alias");
            }

            text = text.Replace("typeof(PhysicsMaterial)", "typeof(PhysicsMaterialCompat)");
            text = text.Replace("new PhysicsMaterial\n", "new PhysicsMaterialCompat\n");
            WriteText(path, text);
        }

        private static void PatchMaterialRenderQueue(string path)
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("GetRawRenderQueue(Material material)"))
            {
                text = ReplaceRequired(text, "RenderQueue = mat.rawRenderQueue,", "RenderQueue = GetRawRenderQueue(mat),", "raw render queue call");
                const string marker = "            return result;\n        }\n\n        [CliCommand(\"set_material_properties\"";
                const string replacement =
                    "            return result;\n        }\n\n" +
                    "        private static int GetRawRenderQueue(Material material)\n" +
                    "        {\n" +
                    "#if UNITY_6000_0_OR_NEWER\n" +
                    "            return material.rawRenderQueue;\n" +
                    "#else\n" +
                    "            var customQueue = new SerializedObject(material).FindProperty(\"m_CustomRenderQueue\");\n" +
                    "            return customQueue != null ? customQueue.intValue : material.renderQueue;\n" +
                    "#endif\n" +
                    "        }\n\n" +
                    "        [CliCommand(\"set_material_properties\"";
                text = ReplaceRequired(text, marker, replacement, "Unity 2022 render queue compatibility");
            }

            WriteText(path, text);
        }

        private static void PatchAnalytics(string path)
        {
            var text = File.ReadAllText(path);
            if (IsUnity2022AnalyticsAdapted(text))
            {
                return;
            }

            text = ReplaceRequired(
                text,
                "namespace Unity.Pipeline.Editor\n{\n    /// <summary>",
                "namespace Unity.Pipeline.Editor\n{\n#if UNITY_6000_0_OR_NEWER\n    /// <summary>",
                "Pipeline analytics version guard");
            var finalClassClose = text.LastIndexOf("    }\n}", StringComparison.Ordinal);
            if (finalClassClose < 0)
            {
                throw new InvalidDataException("Could not locate the PipelineAnalytics class terminator.");
            }

            const string stub =
                "    }\n" +
                "#else\n" +
                "    /// <summary>Unity 2022 compatibility hooks without Unity 6 editor analytics.</summary>\n" +
                "    internal static class PipelineAnalytics\n" +
                "    {\n" +
                "        internal static void RecordCommandExecuted(in CommandExecutionInfo info) { }\n" +
                "        internal static void SendSessionStoppedIfStarted() { }\n" +
                "    }\n" +
                "#endif\n" +
                "}";
            text = text.Substring(0, finalClassClose) + stub + text.Substring(finalClassClose + "    }\n}".Length);
            WriteText(path, text);
        }

        private static bool IsUnity2022AnalyticsAdapted(string text)
        {
            var normalized = (text ?? string.Empty).Replace("\r\n", "\n");
            const string guard = "namespace Unity.Pipeline.Editor\n{\n#if UNITY_6000_0_OR_NEWER\n";
            var guardIndex = normalized.IndexOf(guard, StringComparison.Ordinal);
            var endifIndex = normalized.LastIndexOf("\n#endif\n}", StringComparison.Ordinal);
            if (guardIndex < 0 || endifIndex < 0)
            {
                return false;
            }

            var elseIndex = normalized.LastIndexOf("\n#else\n", endifIndex, StringComparison.Ordinal);
            if (elseIndex <= guardIndex)
            {
                return false;
            }

            var fallback = normalized.Substring(elseIndex, endifIndex - elseIndex);
            return fallback.Contains("internal static class PipelineAnalytics") &&
                   fallback.Contains("RecordCommandExecuted(in CommandExecutionInfo info)") &&
                   fallback.Contains("SendSessionStoppedIfStarted()");
        }

        private static void PatchPipelineDescriptorSelfHealing(string path)
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("Keep discovery metadata alive even when no client is polling /api/status."))
            {
                text = ReplaceRequired(
                    text,
                    "            if (m_HttpListener != null && m_HttpListener.IsListening)\n                return; // healthy\n",
                    "            if (m_HttpListener != null && m_HttpListener.IsListening)\n" +
                    "            {\n" +
                    "                // Keep discovery metadata alive even when no client is polling /api/status.\n" +
                    "                // Rewriting also recreates a descriptor removed while the listener stayed healthy.\n" +
                    "                if (WritesDescriptor)\n" +
                    "                    UpdateHeartBeat();\n" +
                    "                return;\n" +
                    "            }\n",
                    "Pipeline descriptor heartbeat");
            }

            if (!text.Contains("Re-publish discovery metadata after repairing the listener."))
            {
                text = ReplaceRequired(
                    text,
                    "                OpenListener();\n                Debug.Log($\"Pipeline watchdog re-opened HTTP listener on port {m_Port}\");",
                    "                OpenListener();\n" +
                    "                // Re-publish discovery metadata after repairing the listener.\n" +
                    "                if (WritesDescriptor)\n" +
                    "                    UpdateHeartBeat();\n" +
                    "                Debug.Log($\"Pipeline watchdog re-opened HTTP listener on port {m_Port}\");",
                    "Pipeline descriptor republish after listener repair");
            }

            WriteText(path, text);
        }

        private static bool IsPipelineDescriptorSelfHealingAdapted(string text)
        {
            return (text ?? string.Empty).Contains("Keep discovery metadata alive even when no client is polling /api/status.") &&
                   text.Contains("Re-publish discovery metadata after repairing the listener.");
        }

        private static void PatchAssemblyDefinitions(string packageRoot)
        {
            SetEditorOnly(Path.Combine(packageRoot, "CodeGen", "Unity.Pipeline.CodeGen.asmdef"));
            SetEditorOnly(Path.Combine(packageRoot, "Runtime", "Unity.Pipeline.asmdef"));
            SetEditorOnly(Path.Combine(packageRoot, "Runtime", "IlInterpreter", "Unity.Pipeline.IlInterpreter.asmdef"));

            var editorPath = Path.Combine(packageRoot, "Editor", "Unity.Pipeline.Editor.asmdef");
            var editor = JObject.Parse(File.ReadAllText(editorPath));
            RemoveArrayValue(editor, "references", "Unity.Nuget.Newtonsoft-Json");
            EnsureArrayValue(editor, "precompiledReferences", "Newtonsoft.Json.dll");
            WriteJson(editorPath, editor);

            PatchTestAssembly(Path.Combine(packageRoot, "Tests", "Editor", "Unity.Pipeline.Tests.Editor.asmdef"), false);
            PatchTestAssembly(Path.Combine(packageRoot, "Tests", "Runtime", "Unity.Pipeline.Tests.Runtime.asmdef"), true);
        }

        private static void SetEditorOnly(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var assembly = JObject.Parse(File.ReadAllText(path));
            assembly["includePlatforms"] = new JArray("Editor");
            WriteJson(path, assembly);
        }

        private static void PatchTestAssembly(string path, bool editorOnly)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var assembly = JObject.Parse(File.ReadAllText(path));
            RemoveArrayValue(assembly, "references", "UnityEditor.TestRunner");
            RemoveArrayValue(assembly, "references", "UnityEngine.TestRunner");
            assembly["optionalUnityReferences"] = new JArray("TestAssemblies");
            if (editorOnly)
            {
                assembly["includePlatforms"] = new JArray("Editor");
            }
            EnsureArrayValue(assembly, "precompiledReferences", "Newtonsoft.Json.dll");
            EnsureArrayValue(assembly, "defineConstraints", "UNITY_INCLUDE_TESTS");
            EnsureArrayValue(assembly, "defineConstraints", "UNITY_6000_0_OR_NEWER");
            WriteJson(path, assembly);
        }

        private static void PatchCodeAnalysisPlugins(string packageRoot)
        {
            var pluginsRoot = Path.Combine(packageRoot, "Runtime", "Plugins", "CodeAnalysis");
            foreach (var pluginName in CodeAnalysisPluginNames)
            {
                var path = Path.Combine(pluginsRoot, pluginName);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Pipeline CodeAnalysis plugin metadata is missing.", path);
                }

                var guid = File.ReadLines(path)
                    .FirstOrDefault(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                    ?.Substring("guid: ".Length).Trim();
                if (string.IsNullOrEmpty(guid))
                {
                    throw new InvalidDataException("Pipeline plugin metadata has no GUID: " + path);
                }

                WriteText(path, CreateEditorOnlyPluginMeta(guid));
            }
        }

        private static string CreateEditorOnlyPluginMeta(string guid)
        {
            return "fileFormatVersion: 2\n" +
                   "guid: " + guid + "\n" +
                   "PluginImporter:\n" +
                   "  externalObjects: {}\n" +
                   "  serializedVersion: 2\n" +
                   "  iconMap: {}\n" +
                   "  executionOrder: {}\n" +
                   "  defineConstraints: []\n" +
                   "  isPreloaded: 0\n" +
                   "  isOverridable: 1\n" +
                   "  isExplicitlyReferenced: 0\n" +
                   "  validateReferences: 1\n" +
                   "  platformData:\n" +
                   "  - first:\n" +
                   "      Any: \n" +
                   "    second:\n" +
                   "      enabled: 0\n" +
                   "      settings: {}\n" +
                   "  - first:\n" +
                   "      Editor: Editor\n" +
                   "    second:\n" +
                   "      enabled: 1\n" +
                   "      settings:\n" +
                   "        DefaultValueInitialized: true\n" +
                   "  - first:\n" +
                   "      Windows Store Apps: WindowsStoreApps\n" +
                   "    second:\n" +
                   "      enabled: 0\n" +
                   "      settings:\n" +
                   "        CPU: AnyCPU\n" +
                   "  userData: \n" +
                   "  assetBundleName: \n" +
                   "  assetBundleVariant: \n";
        }

        private static void ValidatePipelinePackage(string packageRoot)
        {
            var manifestPath = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("The extracted Pipeline package has no package.json.");
            }

            var package = JObject.Parse(File.ReadAllText(manifestPath));
            var name = package.Value<string>("name");
            var version = package.Value<string>("version");
            if (!string.Equals(name, PipelinePackageName, StringComparison.Ordinal) ||
                !string.Equals(version, Pipeline2022Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected {PipelinePackageName}@{Pipeline2022Version}, found {name}@{version}.");
            }
        }

        private static string ResolveAgentForUnityPackageRoot()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityToolingInstaller).Assembly);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
            {
                throw new InvalidOperationException("Could not resolve the Agent for Unity package path.");
            }

            return package.resolvedPath;
        }

        private static void EnsureAgentsInstruction(string path)
        {
            const string instruction = "When performing Unity development, you must read and follow UNITY-GUIDE.md.";
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (AgentsInstructionExists(path))
            {
                return;
            }

            var prefix = string.IsNullOrWhiteSpace(existing) ? string.Empty : existing.TrimEnd() + "\n\n";
            WriteText(path, prefix + instruction + "\n");
        }

        private static bool AgentsInstructionExists(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            const string instruction = "When performing Unity development, you must read and follow UNITY-GUIDE.md.";
            return File.ReadLines(path).Any(line => string.Equals(line.Trim(), instruction, StringComparison.Ordinal));
        }

        private static void EnsurePipelineLocalExecutionInstruction(string path)
        {
            var text = File.ReadAllText(path);
            if (text.Contains(PipelineLocalExecutionInstruction))
            {
                return;
            }

            const string marker = "\n## 1. Install & verify\n";
            const string heading = "\n## Local Execution Context\n\n";
            var section = heading + PipelineLocalExecutionInstruction + "\n";
            var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
            text = markerIndex >= 0
                ? text.Insert(markerIndex, section)
                : text.TrimEnd() + section + "\n";
            WriteText(path, text);
        }

        private static bool PipelineLocalExecutionInstructionExists(string path)
        {
            return File.Exists(path) && File.ReadAllText(path).Contains(PipelineLocalExecutionInstruction);
        }

        private static bool IsEditorOnlyAssembly(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var assembly = JObject.Parse(File.ReadAllText(path));
            var platforms = assembly["includePlatforms"] as JArray;
            return platforms != null &&
                   platforms.Count == 1 &&
                   string.Equals((string)platforms[0], "Editor", StringComparison.Ordinal);
        }

        private static bool IsUnity2022EditorAssemblyAdapted(string path)
        {
            if (!IsEditorOnlyAssembly(path))
            {
                return false;
            }

            var assembly = JObject.Parse(File.ReadAllText(path));
            return !ArrayContains(assembly, "references", "Unity.Nuget.Newtonsoft-Json") &&
                   ArrayContains(assembly, "precompiledReferences", "Newtonsoft.Json.dll");
        }

        private static bool IsUnity2022TestAssemblyAdapted(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var assembly = JObject.Parse(File.ReadAllText(path));
            return !ArrayContains(assembly, "references", "UnityEditor.TestRunner") &&
                   !ArrayContains(assembly, "references", "UnityEngine.TestRunner") &&
                   ArrayContains(assembly, "optionalUnityReferences", "TestAssemblies") &&
                   ArrayContains(assembly, "precompiledReferences", "Newtonsoft.Json.dll") &&
                   ArrayContains(assembly, "defineConstraints", "UNITY_6000_0_OR_NEWER");
        }

        private static bool IsUnity2022EditorOnlyPlugin(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            return text.Contains("PluginImporter:\n") &&
                   text.Contains("  serializedVersion: 2\n") &&
                   text.Contains("      Any: \n    second:\n      enabled: 0\n") &&
                   text.Contains("      Editor: Editor\n    second:\n      enabled: 1\n");
        }

        private static bool ArrayContains(JObject value, string propertyName, string item)
        {
            var array = value[propertyName] as JArray;
            return array != null &&
                   array.Any(token => string.Equals((string)token, item, StringComparison.Ordinal));
        }

        private static void CopyDirectory(string source, string destination, bool preserveExisting)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException(source);
            }

            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, RelativePath(source, directory)));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, RelativePath(source, file));
                if (preserveExisting && File.Exists(target))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
                File.Copy(file, target, true);
            }
        }

        private static string RelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void AddCandidate(ICollection<string> candidates, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(Path.GetFullPath(path.Trim()));
            }
        }

        private static string ReadPackageVersion(string path)
        {
            try
            {
                return JObject.Parse(File.ReadAllText(path)).Value<string>("version");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteJson(string path, JObject value)
        {
            WriteText(path, value.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");
        }

        private static void WriteText(string path, string value)
        {
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }

        private static string ReplaceRequired(string text, string oldValue, string newValue, string description)
        {
            if (!text.Contains(oldValue))
            {
                throw new InvalidDataException("Could not apply Pipeline patch: " + description + ".");
            }

            return text.Replace(oldValue, newValue);
        }

        private static void RemoveArrayValue(JObject value, string propertyName, string item)
        {
            var array = value[propertyName] as JArray;
            if (array == null)
            {
                return;
            }

            foreach (var token in array.Where(token => string.Equals((string)token, item, StringComparison.Ordinal)).ToList())
            {
                token.Remove();
            }
        }

        private static void EnsureArrayValue(JObject value, string propertyName, string item)
        {
            var array = value[propertyName] as JArray;
            if (array == null)
            {
                array = new JArray();
                value[propertyName] = array;
            }

            if (!array.Any(token => string.Equals((string)token, item, StringComparison.Ordinal)))
            {
                array.Add(item);
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static Task<ProcessResult> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutMilliseconds)
        {
            return Task.Run(() => RunProcess(fileName, arguments, workingDirectory, timeoutMilliseconds));
        }

        private static ProcessResult RunProcess(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutMilliseconds)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    if (!process.Start())
                    {
                        return ProcessResult.Failed("Could not start " + fileName + ".");
                    }

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                        return ProcessResult.Failed(fileName + " timed out.");
                    }

                    Task.WaitAll(new Task[] { stdout, stderr }, 2000);
                    var output = stdout.Status == TaskStatus.RanToCompletion ? stdout.Result : string.Empty;
                    var error = stderr.Status == TaskStatus.RanToCompletion ? stderr.Result : string.Empty;
                    return process.ExitCode == 0
                        ? ProcessResult.Succeeded(output)
                        : ProcessResult.Failed(FirstNonEmptyLine(error, output));
                }
            }
            catch (Exception exception)
            {
                return ProcessResult.Failed(exception.Message);
            }
        }

        private static string FirstNonEmptyLine(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                using (var reader = new StringReader(value))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            return line.Trim();
                        }
                    }
                }
            }

            return string.Empty;
        }

        private sealed class ProcessResult
        {
            private ProcessResult(bool success, string output, string error)
            {
                Success = success;
                Output = output;
                Error = error;
            }

            internal bool Success { get; }
            internal string Output { get; }
            internal string Error { get; }

            internal static ProcessResult Succeeded(string output)
            {
                return new ProcessResult(true, output, null);
            }

            internal static ProcessResult Failed(string error)
            {
                return new ProcessResult(false, null, string.IsNullOrWhiteSpace(error) ? "Unknown process error." : error);
            }
        }
    }
}
