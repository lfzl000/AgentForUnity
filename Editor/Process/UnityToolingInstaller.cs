using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentForUnity.Editor.Application
{
    internal enum UnityToolingBackend
    {
        OfficialPipeline,
        UnityCliLoop
    }

    internal enum UnityToolingSetupPhase
    {
        EnsureCli,
        InstallPackage,
        WaitForPackage,
        InstallSkills,
        UpdateGuide,
        Failed
    }

    [Serializable]
    internal sealed class UnityToolingSetupState
    {
        public string backend;
        public string phase;
        public string error;
        public string updatedAt;

        internal UnityToolingBackend Backend => UnityToolingInstaller.ParseBackend(backend);
        internal UnityToolingSetupPhase Phase => UnityToolingInstaller.ParseSetupPhase(phase);
    }

    internal sealed class ToolingCliInstallation
    {
        internal ToolingCliInstallation(string path, string version, string error)
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

    internal sealed class UnityToolingConnection
    {
        internal UnityToolingConnection(bool isReachable, string endpoint, string status)
        {
            IsReachable = isReachable;
            Endpoint = endpoint;
            Status = status;
        }

        internal bool IsReachable { get; }
        internal string Endpoint { get; }
        internal string Status { get; }
    }

    internal sealed class ToolingPackageInstallation
    {
        internal ToolingPackageInstallation(string resolvedPath, string version)
        {
            ResolvedPath = resolvedPath;
            Version = version;
        }

        internal string ResolvedPath { get; }
        internal string Version { get; }
    }

    internal sealed partial class AgentForUnityService
    {
        private const double UnityToolingRefreshSeconds = 2d;
        private const double ToolingPackageResolveTimeoutSeconds = 300d;

        private UnityToolingBackend _requestedToolingBackend;
        private UnityToolingBackend? _activeToolingBackend;
        private UnityToolingSetupState _toolingSetupState;
        private bool _unityToolingBusy;
        private bool _toolingCliInstalled;
        private bool _toolingPackageInstalled;
        private bool _toolingUnityVersionSupported;
        private bool _toolingProjectSetupComplete;
        private bool _toolingConnectionChecking;
        private bool _toolingConnectionReachable;
        private bool _toolingRefreshPending;
        private int _unityToolingOperation;
        private int _toolingBackendGeneration;
        private double _nextUnityToolingRefreshTime;
        private string _toolingCliPath;
        private string _toolingCliStatus = "Not checked";
        private string _toolingPackageVersion;
        private string _toolingPackageStatus = "Not checked";
        private string _toolingConnectionEndpoint;
        private string _toolingConnectionStatus = "Not checked";

        internal UnityToolingBackend RequestedToolingBackend => _requestedToolingBackend;
        internal UnityToolingBackend? ActiveToolingBackend => _activeToolingBackend;
        internal bool UnityToolingBusy => _unityToolingBusy;
        internal bool ToolingCliInstalled => _toolingCliInstalled;
        internal bool ToolingPackageInstalled => _toolingPackageInstalled;
        internal bool ToolingUnityVersionSupported => _toolingUnityVersionSupported;
        internal bool ToolingProjectSetupComplete => _toolingProjectSetupComplete;
        internal bool ToolingConnectionChecking => _toolingConnectionChecking;
        internal bool ToolingConnectionReachable => _toolingConnectionReachable;
        internal bool ToolingSetupPending => _toolingSetupState != null;
        internal bool ToolingSetupFailed => _toolingSetupState?.Phase == UnityToolingSetupPhase.Failed;
        private bool ToolingSwitchPending => _activeToolingBackend.HasValue &&
                                             _activeToolingBackend.Value != _requestedToolingBackend;
        internal bool ToolingBlocksNewTurns =>
            _unityToolingBusy ||
            ToolingSetupPending && (!ToolingSetupFailed || _activeToolingBackend.HasValue) ||
            ToolingSwitchPending;
        private static bool UnityEditorBusyForTooling => EditorApplication.isCompiling || EditorApplication.isUpdating;
        internal string ToolingCliStatus => _toolingCliStatus;
        internal string ToolingPackageStatus => _toolingPackageStatus;
        internal string ToolingConnectionStatus => _toolingConnectionStatus;
        internal string ToolingConnectionEndpoint => _toolingConnectionEndpoint;
        internal string ToolingCliToolPath => _toolingCliPath;
        internal bool CanChangeToolingBackend => !_disposed &&
                                                 !_unityToolingBusy &&
                                                 !IsTurnStarting &&
                                                 !_operationInProgress &&
                                                 !UnityEditorBusyForTooling &&
                                                 UnityToolingInstaller.IsUnity6OrNewer(UnityEngine.Application.unityVersion) &&
                                                 (_toolingSetupState == null || ToolingSetupFailed);
        internal bool CanInstallToolingCli => !_disposed &&
                                              !_unityToolingBusy &&
                                              !IsTurnStarting &&
                                              !_operationInProgress &&
                                              !UnityEditorBusyForTooling &&
                                              _toolingUnityVersionSupported &&
                                              !_toolingCliInstalled &&
                                              (_toolingSetupState == null || ToolingSetupFailed);
        internal bool CanInstallToolingPackage => !_disposed &&
                                                  !_unityToolingBusy &&
                                                  !IsTurnStarting &&
                                                  !_operationInProgress &&
                                                  !UnityEditorBusyForTooling &&
                                                  _toolingUnityVersionSupported &&
                                                  (_toolingSetupState == null || ToolingSetupFailed) &&
                                                  (!_toolingProjectSetupComplete ||
                                                   _activeToolingBackend != _requestedToolingBackend);

        private void InitializeUnityToolingState()
        {
            var unityVersion = UnityEngine.Application.unityVersion;
            var defaultBackend = UnityToolingInstaller.GetDefaultBackend(unityVersion);
            _requestedToolingBackend = UnityToolingInstaller.TryParseBackend(
                    _persistedState.requestedToolingBackend,
                    out var requested) &&
                UnityToolingInstaller.IsBackendSupported(requested, unityVersion)
                    ? requested
                    : defaultBackend;

            if (UnityToolingInstaller.TryParseBackend(_persistedState.activeToolingBackend, out var active) &&
                UnityToolingInstaller.IsBackendSupported(active, unityVersion))
            {
                _activeToolingBackend = active;
            }
            else
            {
                _activeToolingBackend = null;
            }

            try
            {
                _toolingSetupState = UnityToolingInstaller.LoadSetupState(_projectRoot);
                if (_toolingSetupState != null &&
                    UnityToolingInstaller.IsBackendSupported(_toolingSetupState.Backend, unityVersion))
                {
                    _requestedToolingBackend = _toolingSetupState.Backend;
                }
            }
            catch (Exception exception)
            {
                _toolingSetupState = UnityToolingInstaller.CreateSetupState(
                    _requestedToolingBackend,
                    UnityToolingSetupPhase.Failed);
                _toolingSetupState.error = "Setup state invalid · " + exception.Message;
                AddDiagnostic("Could not restore Unity tooling setup: " + exception.Message);
            }

            SaveUnityToolingState();
            RefreshToolingInstallation();
        }

        private void SaveUnityToolingState()
        {
            _persistedState.requestedToolingBackend = UnityToolingInstaller.BackendToken(_requestedToolingBackend);
            _persistedState.activeToolingBackend = _activeToolingBackend.HasValue
                ? UnityToolingInstaller.BackendToken(_activeToolingBackend.Value)
                : null;
        }

        internal void SelectToolingBackend(UnityToolingBackend backend)
        {
            if (_requestedToolingBackend == backend ||
                !CanChangeToolingBackend ||
                !UnityToolingInstaller.IsBackendSupported(backend, UnityEngine.Application.unityVersion))
            {
                return;
            }

            _requestedToolingBackend = backend;
            _toolingBackendGeneration++;
            _unityToolingOperation++;
            _toolingConnectionChecking = false;
            _toolingConnectionReachable = false;
            _toolingConnectionEndpoint = null;
            _toolingConnectionStatus = "Not checked";
            var cancelledFailedSwitch = TryCancelFailedToolingSwitch(backend);
            SaveState();
            RefreshToolingInstallation();
            MarkChanged();
            if (cancelledFailedSwitch)
            {
                ReconnectForToolingInstructionChange();
            }
            RefreshUnityTooling();
        }

        private bool TryCancelFailedToolingSwitch(UnityToolingBackend backend)
        {
            if (!ToolingSetupFailed ||
                !_activeToolingBackend.HasValue ||
                _activeToolingBackend.Value != backend ||
                _toolingSetupState.Backend == backend ||
                UnityToolingInstaller.FindPackage(backend, _projectRoot) == null ||
                !UnityToolingInstaller.IsProjectSkillSetupReady(backend, _projectRoot))
            {
                return false;
            }

            try
            {
                UnityToolingInstaller.UpdateManagedGuide(_projectRoot, backend, "active");
                UnityToolingInstaller.ClearSetupState(_projectRoot);
                _toolingSetupState = null;
                return true;
            }
            catch (Exception exception)
            {
                AddDiagnostic("Could not cancel the failed Unity tooling switch: " + exception.Message);
                return false;
            }
        }

        internal async void RefreshUnityTooling()
        {
            if (_disposed)
            {
                return;
            }

            if (_unityToolingBusy ||
                IsTurnStarting ||
                _operationInProgress ||
                UnityEditorBusyForTooling)
            {
                _toolingRefreshPending = true;
                return;
            }

            _toolingRefreshPending = false;
            ReloadToolingSetupState();
            var backend = _requestedToolingBackend;
            var generation = _toolingBackendGeneration;
            var operation = BeginUnityToolingOperation("Detecting tooling CLI", "Checking tooling package");
            try
            {
                var cli = await UnityToolingInstaller.DetectCliAsync(backend);
                if (!IsCurrentUnityToolingOperation(operation, backend, generation))
                {
                    return;
                }

                ApplyToolingCliDetection(cli);
                RefreshToolingInstallation();
                await RefreshToolingConnectionStatusAsync(operation, backend, generation);
            }
            catch (Exception exception)
            {
                if (IsCurrentUnityToolingOperation(operation, backend, generation))
                {
                    if (!_toolingCliInstalled)
                    {
                        _toolingCliStatus = "Detection failed";
                    }
                    _toolingPackageStatus = _toolingPackageInstalled
                        ? "Project setup failed"
                        : "Detection failed";
                    AddDiagnostic("Unity tooling detection failed: " + exception.Message);
                }
            }
            finally
            {
                EndUnityToolingOperation(operation);
            }
        }

        internal async void InstallToolingCli()
        {
            if (!CanInstallToolingCli)
            {
                return;
            }

            var backend = _requestedToolingBackend;
            var generation = _toolingBackendGeneration;
            var operation = BeginUnityToolingOperation("Installing tooling CLI", _toolingPackageStatus);
            try
            {
                var cli = await UnityToolingInstaller.EnsureCliInstalledAsync(backend);
                if (!IsCurrentUnityToolingOperation(operation, backend, generation))
                {
                    return;
                }

                ApplyToolingCliDetection(cli);
                if (!cli.IsAvailable)
                {
                    throw new InvalidOperationException(cli.Error ?? "Tooling CLI installation failed.");
                }
            }
            catch (Exception exception)
            {
                if (IsCurrentUnityToolingOperation(operation, backend, generation))
                {
                    _toolingCliInstalled = false;
                    _toolingCliStatus = "Installation failed";
                    AddDiagnostic(ToolingBackendName(backend) + " CLI installation failed: " + exception.Message);
                }
            }
            finally
            {
                EndUnityToolingOperation(operation);
            }
        }

        internal void InstallToolingBackend()
        {
            if (!CanInstallToolingPackage)
            {
                return;
            }

            var setup = UnityToolingInstaller.CreateSetupState(
                _requestedToolingBackend,
                UnityToolingSetupPhase.EnsureCli);
            if (_requestedToolingBackend == UnityToolingBackend.OfficialPipeline &&
                UnityToolingInstaller.IsLegacyAdaptedPipelinePackage(_projectRoot))
            {
                FailToolingSetup(
                    setup,
                    new InvalidOperationException(UnityToolingInstaller.LegacyAdaptedPipelineMigrationMessage));
                return;
            }

            try
            {
                UnityToolingInstaller.SaveSetupState(_projectRoot, setup);
                _toolingSetupState = setup;
                UnityToolingInstaller.UpdateManagedGuide(_projectRoot, setup.Backend, "pending");
                SaveState();
                ContinueToolingSetup(setup);
            }
            catch (Exception exception)
            {
                FailToolingSetup(setup, exception);
            }
        }

        internal void ResumePendingUnityToolingSetup()
        {
            if (_disposed ||
                _unityToolingBusy ||
                IsTurnStarting ||
                _operationInProgress ||
                UnityEditorBusyForTooling)
            {
                return;
            }

            ReloadToolingSetupState();
            var setup = _toolingSetupState;
            if (setup == null || setup.Phase == UnityToolingSetupPhase.Failed)
            {
                return;
            }
            if (!UnityToolingInstaller.IsBackendSupported(setup.Backend, UnityEngine.Application.unityVersion))
            {
                FailToolingSetup(setup, new InvalidOperationException("The pending backend is not supported by this Unity version."));
                return;
            }

            if (_requestedToolingBackend != setup.Backend)
            {
                _requestedToolingBackend = setup.Backend;
                _toolingBackendGeneration++;
                SaveState();
            }

            if (setup.Phase == UnityToolingSetupPhase.WaitForPackage)
            {
                RefreshToolingInstallation();
                if (!_toolingPackageInstalled)
                {
                    if (UnityToolingInstaller.HasSetupTimedOut(
                            setup,
                            TimeSpan.FromSeconds(ToolingPackageResolveTimeoutSeconds)))
                    {
                        FailToolingSetup(
                            setup,
                            new TimeoutException("Unity did not resolve the tooling package within five minutes."));
                        return;
                    }

                    _toolingPackageStatus = "Resolving and compiling tooling package";
                    MarkChanged();
                    return;
                }

                SetSetupPhase(setup, UnityToolingSetupPhase.InstallSkills);
            }

            ContinueToolingSetup(setup);
        }

        private async void ContinueToolingSetup(UnityToolingSetupState setup)
        {
            if (_disposed ||
                _unityToolingBusy ||
                IsTurnStarting ||
                _operationInProgress ||
                UnityEditorBusyForTooling ||
                setup == null)
            {
                return;
            }

            var backend = setup.Backend;
            var generation = _toolingBackendGeneration;
            var operation = BeginUnityToolingOperation(_toolingCliStatus, SetupPhaseStatus(setup.Phase));
            try
            {
                ToolingCliInstallation cli = null;
                if (setup.Phase == UnityToolingSetupPhase.EnsureCli)
                {
                    _toolingCliStatus = "Installing tooling CLI";
                    MarkChanged();
                    cli = await UnityToolingInstaller.EnsureCliInstalledAsync(backend);
                    if (ShouldPauseUnityToolingContinuation(operation, backend, generation))
                    {
                        return;
                    }

                    ApplyToolingCliDetection(cli);
                    if (!cli.IsAvailable)
                    {
                        throw new InvalidOperationException(cli.Error ?? "Tooling CLI installation failed.");
                    }

                    SetSetupPhase(setup, UnityToolingSetupPhase.InstallPackage);
                }

                if (setup.Phase == UnityToolingSetupPhase.InstallPackage)
                {
                    RefreshToolingInstallation();
                    if (!_toolingPackageInstalled)
                    {
                        if (cli == null)
                        {
                            cli = await UnityToolingInstaller.DetectCliAsync(backend);
                        }
                        if (ShouldPauseUnityToolingContinuation(operation, backend, generation))
                        {
                            return;
                        }
                        if (!cli.IsAvailable)
                        {
                            throw new InvalidOperationException(cli.Error ?? "The tooling CLI is unavailable.");
                        }

                        // Persist WaitForPackage before the external command can trigger Package Manager and Domain Reload.
                        SetSetupPhase(setup, UnityToolingSetupPhase.WaitForPackage);
                        _toolingPackageStatus = "Installing tooling package";
                        MarkChanged();
                        await UnityToolingInstaller.InstallPackageAsync(backend, cli.Path, _projectRoot);
                        if (ShouldPauseUnityToolingContinuation(operation, backend, generation))
                        {
                            return;
                        }

                        // Reset the timeout after the external installer finishes, then let UPM own
                        // package import, script compilation, and any resulting Domain Reload.
                        SetSetupPhase(setup, UnityToolingSetupPhase.WaitForPackage);
                        UnityEditor.PackageManager.Client.Resolve();
                        return;
                    }

                    SetSetupPhase(setup, UnityToolingSetupPhase.InstallSkills);
                }

                if (setup.Phase == UnityToolingSetupPhase.WaitForPackage)
                {
                    RefreshToolingInstallation();
                    if (!_toolingPackageInstalled)
                    {
                        _toolingPackageStatus = "Resolving and compiling tooling package";
                        return;
                    }

                    SetSetupPhase(setup, UnityToolingSetupPhase.InstallSkills);
                }

                if (setup.Phase == UnityToolingSetupPhase.InstallSkills)
                {
                    var package = UnityToolingInstaller.FindPackage(backend, _projectRoot);
                    if (package == null)
                    {
                        SetSetupPhase(setup, UnityToolingSetupPhase.WaitForPackage);
                        _toolingPackageStatus = "Resolving and compiling tooling package";
                        return;
                    }

                    if (cli == null)
                    {
                        cli = await UnityToolingInstaller.DetectCliAsync(backend);
                    }
                    if (ShouldPauseUnityToolingContinuation(operation, backend, generation))
                    {
                        return;
                    }
                    ApplyToolingCliDetection(cli);
                    if (!cli.IsAvailable)
                    {
                        throw new InvalidOperationException(cli.Error ?? "The tooling CLI is unavailable.");
                    }

                    _toolingPackageStatus = "Installing project skills";
                    MarkChanged();
                    await UnityToolingInstaller.InstallProjectSkillsAsync(
                        backend,
                        cli?.Path,
                        _projectRoot,
                        package.ResolvedPath);
                    if (ShouldPauseUnityToolingContinuation(operation, backend, generation))
                    {
                        return;
                    }

                    SetSetupPhase(setup, UnityToolingSetupPhase.UpdateGuide);
                }

                if (setup.Phase == UnityToolingSetupPhase.UpdateGuide)
                {
                    if (IsTurnStarting || _operationInProgress || UnityEditorBusyForTooling)
                    {
                        return;
                    }

                    if (cli == null)
                    {
                        cli = await UnityToolingInstaller.DetectCliAsync(backend);
                    }
                    if (ShouldPauseUnityToolingContinuation(operation, backend, generation))
                    {
                        return;
                    }
                    ApplyToolingCliDetection(cli);
                    if (!cli.IsAvailable)
                    {
                        throw new InvalidOperationException(cli.Error ?? "The tooling CLI is unavailable.");
                    }
                    if (UnityToolingInstaller.FindPackage(backend, _projectRoot) == null)
                    {
                        SetSetupPhase(setup, UnityToolingSetupPhase.WaitForPackage);
                        return;
                    }
                    if (!UnityToolingInstaller.IsProjectSkillSetupReady(backend, _projectRoot))
                    {
                        SetSetupPhase(setup, UnityToolingSetupPhase.InstallSkills);
                        return;
                    }

                    UnityToolingInstaller.UpdateManagedGuide(_projectRoot, backend, "active");
                    var previousActive = _activeToolingBackend;
                    _activeToolingBackend = backend;
                    if (!SaveState())
                    {
                        _activeToolingBackend = previousActive;
                        SaveUnityToolingState();
                        throw new IOException("Could not persist the active Unity tooling backend.");
                    }

                    UnityToolingInstaller.ClearSetupState(_projectRoot);
                    _toolingSetupState = null;
                    RefreshToolingInstallation();
                    _toolingConnectionChecking = false;
                    _toolingConnectionReachable = false;
                    _toolingConnectionEndpoint = null;
                    _toolingConnectionStatus = "Not checked";
                    MarkChanged();

                    if (_started && ConnectionState != AgentConnectionState.Disconnected)
                    {
                        Reconnect();
                    }
                }
            }
            catch (Exception exception)
            {
                if (IsCurrentUnityToolingOperation(operation, backend, generation))
                {
                    FailToolingSetup(setup, exception);
                }
            }
            finally
            {
                EndUnityToolingOperation(operation);
            }
        }

        private void FailToolingSetup(UnityToolingSetupState setup, Exception exception)
        {
            if (setup == null)
            {
                return;
            }

            setup.phase = UnityToolingInstaller.SetupPhaseToken(UnityToolingSetupPhase.Failed);
            setup.error = exception?.Message ?? "Unknown setup error.";
            setup.updatedAt = DateTime.UtcNow.ToString("O");
            try
            {
                UnityToolingInstaller.SaveSetupState(_projectRoot, setup);
                UnityToolingInstaller.UpdateManagedGuide(_projectRoot, setup.Backend, "failed");
            }
            catch (Exception persistenceException)
            {
                AddDiagnostic("Could not persist failed Unity tooling setup: " + persistenceException.Message);
            }

            _toolingSetupState = setup;
            _toolingProjectSetupComplete = false;
            _toolingPackageStatus = "Setup failed · " + setup.error;
            AddDiagnostic(ToolingBackendName(setup.Backend) + " setup failed: " + setup.error);
            MarkChanged();
            ReconnectForToolingInstructionChange();
        }

        private void SetSetupPhase(UnityToolingSetupState setup, UnityToolingSetupPhase phase)
        {
            setup.phase = UnityToolingInstaller.SetupPhaseToken(phase);
            setup.error = null;
            setup.updatedAt = DateTime.UtcNow.ToString("O");
            UnityToolingInstaller.SaveSetupState(_projectRoot, setup);
            _toolingSetupState = setup;
            _toolingPackageStatus = SetupPhaseStatus(phase);
            MarkChanged();
        }

        private void ReloadToolingSetupState()
        {
            var invalidStateAlreadyReported = _toolingSetupState != null &&
                                              _toolingSetupState.Phase == UnityToolingSetupPhase.Failed &&
                                              (_toolingSetupState.error ?? string.Empty)
                                              .StartsWith("Setup state invalid", StringComparison.Ordinal);
            try
            {
                _toolingSetupState = UnityToolingInstaller.LoadSetupState(_projectRoot);
            }
            catch (Exception exception)
            {
                _toolingSetupState = UnityToolingInstaller.CreateSetupState(
                    _requestedToolingBackend,
                    UnityToolingSetupPhase.Failed);
                _toolingSetupState.error = "Setup state invalid · " + exception.Message;
                _toolingProjectSetupComplete = false;
                _toolingPackageStatus = "Setup state invalid";
                if (!invalidStateAlreadyReported)
                {
                    AddDiagnostic("Could not read Unity tooling setup state: " + exception.Message);
                    ReconnectForToolingInstructionChange();
                }
            }
        }

        private void ReconnectForToolingInstructionChange()
        {
            if (!_disposed &&
                _started &&
                !IsTurnStarting &&
                ConnectionState != AgentConnectionState.Disconnected)
            {
                Reconnect();
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
                if (_toolingRefreshPending &&
                    !IsTurnStarting &&
                    !_operationInProgress &&
                    !UnityEditorBusyForTooling)
                {
                    RefreshUnityTooling();
                }
                ResumePendingUnityToolingSetup();
            }

            // Keep network and process probes event-driven. Repeated probes can exhaust
            // Unity 2022 Mono's IOSelector registrations during long Editor sessions.
        }

        private async Task RefreshToolingConnectionStatusAsync(
            int toolingOperation,
            UnityToolingBackend backend,
            int generation)
        {
            if (_disposed || _toolingConnectionChecking ||
                IsTurnStarting ||
                _operationInProgress ||
                backend != _requestedToolingBackend || generation != _toolingBackendGeneration)
            {
                return;
            }

            if (!_toolingProjectSetupComplete)
            {
                _toolingConnectionReachable = false;
                _toolingConnectionEndpoint = null;
                _toolingConnectionStatus = "Unavailable · Backend is not active";
                MarkChanged();
                return;
            }

            _toolingConnectionChecking = true;
            _toolingConnectionStatus = "Checking connection";
            MarkChanged();
            try
            {
                var connection = await UnityToolingInstaller.DetectConnectionAsync(
                    backend,
                    _toolingCliPath,
                    _projectRoot);
                if (!IsCurrentToolingConnection(toolingOperation, backend, generation))
                {
                    return;
                }

                _toolingConnectionReachable = connection.IsReachable;
                _toolingConnectionEndpoint = connection.Endpoint;
                _toolingConnectionStatus = connection.Status;
            }
            catch (Exception exception)
            {
                if (IsCurrentToolingConnection(toolingOperation, backend, generation))
                {
                    _toolingConnectionReachable = false;
                    _toolingConnectionEndpoint = null;
                    _toolingConnectionStatus = "Detection failed · " + exception.Message;
                }
            }
            finally
            {
                if (backend == _requestedToolingBackend && generation == _toolingBackendGeneration)
                {
                    _toolingConnectionChecking = false;
                    if (!_disposed)
                    {
                        MarkChanged();
                    }
                }
            }
        }

        private bool IsCurrentToolingConnection(
            int toolingOperation,
            UnityToolingBackend backend,
            int generation)
        {
            return !_disposed &&
                   backend == _requestedToolingBackend &&
                   generation == _toolingBackendGeneration &&
                   (toolingOperation == 0 || toolingOperation == _unityToolingOperation);
        }

        private int BeginUnityToolingOperation(string cliStatus, string packageStatus)
        {
            _unityToolingBusy = true;
            var operation = ++_unityToolingOperation;
            _toolingCliStatus = cliStatus;
            _toolingPackageStatus = packageStatus;
            MarkChanged();
            return operation;
        }

        private bool IsCurrentUnityToolingOperation(
            int operation,
            UnityToolingBackend backend,
            int generation)
        {
            return !_disposed &&
                   operation == _unityToolingOperation &&
                   backend == _requestedToolingBackend &&
                   generation == _toolingBackendGeneration;
        }

        private bool ShouldPauseUnityToolingContinuation(
            int operation,
            UnityToolingBackend backend,
            int generation)
        {
            return !IsCurrentUnityToolingOperation(operation, backend, generation) ||
                   IsTurnStarting ||
                   _operationInProgress ||
                   UnityEditorBusyForTooling;
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

        private void ApplyToolingCliDetection(ToolingCliInstallation cli)
        {
            _toolingCliInstalled = cli.IsAvailable;
            _toolingCliPath = cli.Path;
            _toolingCliStatus = cli.IsAvailable
                ? "Installed" + (string.IsNullOrEmpty(cli.Version) ? string.Empty : " · " + cli.Version)
                : "Not installed";
            if (!cli.IsAvailable && !string.IsNullOrWhiteSpace(cli.Error))
            {
                _toolingCliStatus += " · " + cli.Error;
            }
        }

        private void RefreshToolingInstallation()
        {
            var backend = _requestedToolingBackend;
            var hasLegacyAdaptedPipeline = backend == UnityToolingBackend.OfficialPipeline &&
                                           UnityToolingInstaller.IsLegacyAdaptedPipelinePackage(_projectRoot);
            var package = UnityToolingInstaller.FindPackage(backend, _projectRoot);
            _toolingPackageInstalled = package != null;
            _toolingPackageVersion = package?.Version;
            _toolingUnityVersionSupported = UnityToolingInstaller.IsBackendSupported(
                backend,
                UnityEngine.Application.unityVersion);
            _toolingProjectSetupComplete = _toolingPackageInstalled &&
                                           _toolingUnityVersionSupported &&
                                           _toolingSetupState == null &&
                                           _activeToolingBackend == backend &&
                                           UnityToolingInstaller.IsProjectSupportReady(backend, _projectRoot);

            var versionSuffix = string.IsNullOrEmpty(_toolingPackageVersion)
                ? string.Empty
                : " · " + _toolingPackageVersion;
            if (!_toolingUnityVersionSupported)
            {
                _toolingPackageStatus = _toolingPackageInstalled
                    ? "Installed" + versionSuffix + " · Unsupported Unity version"
                    : "Unsupported Unity version";
                return;
            }
            if (_toolingSetupState != null)
            {
                _toolingPackageStatus = _toolingSetupState.Phase == UnityToolingSetupPhase.Failed
                    ? "Setup failed · " + (_toolingSetupState.error ?? "Unknown error")
                    : SetupPhaseStatus(_toolingSetupState.Phase);
                return;
            }
            if (hasLegacyAdaptedPipeline)
            {
                _toolingPackageStatus = "Legacy adapted package · Remove Packages/com.unity.pipeline before setup";
                return;
            }
            if (!_toolingPackageInstalled)
            {
                _toolingPackageStatus = "Not installed";
                return;
            }

            _toolingPackageStatus = _toolingProjectSetupComplete
                ? "Installed" + versionSuffix + " · Skills ready · Active"
                : "Installed" + versionSuffix + " · Not active";
        }

        private string GetUnityToolingDeveloperInstructions()
        {
            UnityToolingSetupState setup;
            try
            {
                setup = UnityToolingInstaller.LoadSetupState(_projectRoot);
            }
            catch (Exception)
            {
                setup = new UnityToolingSetupState();
            }

            if (setup != null)
            {
                return "Unity Editor tooling setup or a backend switch is pending or failed. Do not use any " +
                       "Unity Editor bridge, including Unity CLI/Pipeline, uloop, Unity MCP, or related skills. " +
                       "Directly edit only files allowed by UNITY-GUIDE.md and report Editor validation as unavailable.";
            }

            if (!_activeToolingBackend.HasValue ||
                !UnityToolingInstaller.IsBackendSupported(
                    _activeToolingBackend.Value,
                    UnityEngine.Application.unityVersion) ||
                UnityToolingInstaller.FindPackage(_activeToolingBackend.Value, _projectRoot) == null ||
                !UnityToolingInstaller.IsProjectSupportReady(_activeToolingBackend.Value, _projectRoot))
            {
                return "No Unity Editor tooling backend is active. Do not use Unity CLI/Pipeline, uloop, Unity MCP, " +
                       "or related Editor-control skills. Directly edit only files allowed by UNITY-GUIDE.md and " +
                       "report Editor validation as unavailable.";
            }

            return _activeToolingBackend.Value == UnityToolingBackend.OfficialPipeline
                ? "The active Unity Editor backend is officialPipeline. Use only the official Unity CLI with the " +
                  "com.unity.pipeline package and its unity-pipeline skill. Do not invoke uloop or Unity MCP."
                : "The active Unity Editor backend is unityCliLoop. Use only the uloop CLI, the " +
                  "io.github.hatayama.uloopmcp package, and installed uloop skills. Do not invoke unity pipeline, " +
                  "unity command, or Unity MCP.";
        }

        private static string ToolingBackendName(UnityToolingBackend backend)
        {
            return backend == UnityToolingBackend.OfficialPipeline
                ? "Official Unity CLI + Pipeline"
                : "Unity CLI Loop";
        }

        private static string SetupPhaseStatus(UnityToolingSetupPhase phase)
        {
            switch (phase)
            {
                case UnityToolingSetupPhase.EnsureCli:
                    return "Preparing tooling CLI";
                case UnityToolingSetupPhase.InstallPackage:
                    return "Preparing tooling package";
                case UnityToolingSetupPhase.WaitForPackage:
                    return "Resolving and compiling tooling package";
                case UnityToolingSetupPhase.InstallSkills:
                    return "Installing project skills";
                case UnityToolingSetupPhase.UpdateGuide:
                    return "Activating backend";
                case UnityToolingSetupPhase.Failed:
                    return "Setup failed";
                default:
                    return "Setting up tooling";
            }
        }
    }

    internal static class UnityToolingInstaller
    {
        internal const string OfficialPipelineToken = "officialPipeline";
        internal const string UnityCliLoopToken = "unityCliLoop";
        internal const string LegacyAdaptedPipelineMigrationMessage =
            "A legacy Unity 2022 adapted Pipeline package is embedded at Packages/com.unity.pipeline. " +
            "Back up any local changes, remove that directory, and retry so Unity CLI can install the official package.";

        private const string PipelinePackageName = "com.unity.pipeline";
        private const string LegacyAdaptedPipelineVersion = "0.6.0-exp.1";
        private const string UnityCliLoopPackageName = "io.github.hatayama.uloopmcp";
        private const int UnityCliLoopMinimumMajorVersion = 3;
        private const string GuideBeginMarker = "<!-- AGENT_FOR_UNITY_TOOLING_BEGIN -->";
        private const string GuideEndMarker = "<!-- AGENT_FOR_UNITY_TOOLING_END -->";
        internal const string AgentsGuideInstruction =
            "When performing Unity development, you must read and follow UNITY-GUIDE.md.";
        private const string PipelineLocalExecutionInstruction =
            "For every `unity pipeline` or `unity command` invocation that connects to an Editor or Player on localhost, request the approved local execution context on the first attempt. Do not probe the loopback endpoint from the restricted sandbox first. Use narrow reusable command-prefix approval rules such as `unity pipeline list` and `unity command` when the host supports them.";

        internal static UnityToolingBackend GetDefaultBackend(string unityVersion)
        {
            return IsUnity6OrNewer(unityVersion)
                ? UnityToolingBackend.OfficialPipeline
                : UnityToolingBackend.UnityCliLoop;
        }

        internal static bool IsBackendSupported(UnityToolingBackend backend, string unityVersion)
        {
            if (!IsUnity2022_3OrNewer(unityVersion))
            {
                return false;
            }

            return backend == UnityToolingBackend.UnityCliLoop || IsUnity6OrNewer(unityVersion);
        }

        internal static bool IsUnity6OrNewer(string unityVersion)
        {
            return TryParseUnityVersion(unityVersion, out var major, out _) && major >= 6000;
        }

        internal static bool IsUnity2022_3OrNewer(string unityVersion)
        {
            if (!TryParseUnityVersion(unityVersion, out var major, out var minor))
            {
                return false;
            }

            return major > 2022 || major == 2022 && minor >= 3;
        }

        internal static string BackendToken(UnityToolingBackend backend)
        {
            return backend == UnityToolingBackend.OfficialPipeline
                ? OfficialPipelineToken
                : UnityCliLoopToken;
        }

        internal static bool TryParseBackend(string value, out UnityToolingBackend backend)
        {
            if (string.Equals(value, OfficialPipelineToken, StringComparison.Ordinal))
            {
                backend = UnityToolingBackend.OfficialPipeline;
                return true;
            }
            if (string.Equals(value, UnityCliLoopToken, StringComparison.Ordinal))
            {
                backend = UnityToolingBackend.UnityCliLoop;
                return true;
            }

            backend = UnityToolingBackend.UnityCliLoop;
            return false;
        }

        internal static UnityToolingBackend ParseBackend(string value)
        {
            if (!TryParseBackend(value, out var backend))
            {
                throw new InvalidDataException("Unknown Unity tooling backend: " + (value ?? "<null>"));
            }

            return backend;
        }

        internal static string SetupPhaseToken(UnityToolingSetupPhase phase)
        {
            switch (phase)
            {
                case UnityToolingSetupPhase.EnsureCli:
                    return "ensureCli";
                case UnityToolingSetupPhase.InstallPackage:
                    return "installPackage";
                case UnityToolingSetupPhase.WaitForPackage:
                    return "waitForPackage";
                case UnityToolingSetupPhase.InstallSkills:
                    return "installSkills";
                case UnityToolingSetupPhase.UpdateGuide:
                    return "updateGuide";
                case UnityToolingSetupPhase.Failed:
                    return "failed";
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
            }
        }

        internal static UnityToolingSetupPhase ParseSetupPhase(string value)
        {
            switch (value)
            {
                case "ensureCli":
                    return UnityToolingSetupPhase.EnsureCli;
                case "installPackage":
                    return UnityToolingSetupPhase.InstallPackage;
                case "waitForPackage":
                    return UnityToolingSetupPhase.WaitForPackage;
                case "installSkills":
                    return UnityToolingSetupPhase.InstallSkills;
                case "updateGuide":
                    return UnityToolingSetupPhase.UpdateGuide;
                case "failed":
                    return UnityToolingSetupPhase.Failed;
                default:
                    throw new InvalidDataException("Unknown Unity tooling setup phase: " + (value ?? "<null>"));
            }
        }

        internal static UnityToolingSetupState CreateSetupState(
            UnityToolingBackend backend,
            UnityToolingSetupPhase phase)
        {
            return new UnityToolingSetupState
            {
                backend = BackendToken(backend),
                phase = SetupPhaseToken(phase),
                updatedAt = DateTime.UtcNow.ToString("O")
            };
        }

        internal static bool HasSetupTimedOut(UnityToolingSetupState state, TimeSpan timeout)
        {
            if (state == null ||
                !DateTime.TryParse(
                    state.updatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var updatedAt))
            {
                return true;
            }

            return DateTime.UtcNow - updatedAt.ToUniversalTime() >= timeout;
        }

        internal static Task<ToolingCliInstallation> DetectCliAsync(UnityToolingBackend backend)
        {
            return Task.Run(() => DetectCli(backend));
        }

        internal static async Task<ToolingCliInstallation> EnsureCliInstalledAsync(UnityToolingBackend backend)
        {
            var current = await DetectCliAsync(backend);
            if (current.IsAvailable)
            {
                return current;
            }

            var result = backend == UnityToolingBackend.OfficialPipeline
                ? await RunOfficialUnityCliInstallerAsync()
                : await RunUnityCliLoopInstallerAsync();
            if (!result.Success)
            {
                return new ToolingCliInstallation(null, null, result.Error);
            }

            return await DetectCliAsync(backend);
        }

        internal static async Task InstallPackageAsync(
            UnityToolingBackend backend,
            string cliPath,
            string projectRoot)
        {
            var arguments = backend == UnityToolingBackend.OfficialPipeline
                ? "--non-interactive --no-banner pipeline install --project-path " + QuoteArgument(projectRoot)
                : "package install";
            var result = await RunProcessAsync(cliPath, arguments, projectRoot, 180000);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error);
            }
        }

        internal static async Task InstallProjectSkillsAsync(
            UnityToolingBackend backend,
            string cliPath,
            string projectRoot,
            string packageRoot)
        {
            if (backend == UnityToolingBackend.OfficialPipeline)
            {
                var projectSkillsRoot = Path.Combine(projectRoot, ".agents", "skills");
                var source = Path.Combine(packageRoot, ".claude", "skills", "unity-pipeline");
                var target = Path.Combine(projectSkillsRoot, "unity-pipeline");
                if (!File.Exists(Path.Combine(source, "SKILL.md")))
                {
                    throw new FileNotFoundException(
                        "The Pipeline package does not contain its unity-pipeline skill.",
                        source);
                }

                CopyDirectory(source, target, true);
                EnsurePipelineLocalExecutionInstruction(Path.Combine(target, "SKILL.md"));
            }
            else
            {
                if (string.IsNullOrEmpty(cliPath))
                {
                    throw new InvalidOperationException("uloop CLI is unavailable.");
                }

                var result = await RunProcessAsync(cliPath, "skills install --agents", projectRoot, 180000);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Error);
                }

                var verification = await RunProcessAsync(
                    cliPath,
                    "skills list --agents",
                    projectRoot,
                    60000);
                if (!verification.Success)
                {
                    throw new InvalidOperationException(
                        "Could not verify installed Unity CLI Loop skills: " + verification.Error);
                }

                var status = verification.Output ?? string.Empty;
                if (status.Contains("(not installed)") ||
                    status.Contains("(outdated)") ||
                    status.Contains("(conflict)") ||
                    !status.Contains("(installed)"))
                {
                    throw new InvalidOperationException(
                        "Unity CLI Loop did not report at least one complete .agents skill installation.");
                }
            }

        }

        internal static ToolingPackageInstallation FindPackage(
            UnityToolingBackend backend,
            string projectRoot)
        {
            var packageName = backend == UnityToolingBackend.OfficialPipeline
                ? PipelinePackageName
                : UnityCliLoopPackageName;
            var embeddedPath = Path.Combine(projectRoot, "Packages", packageName);
            var embeddedManifest = Path.Combine(embeddedPath, "package.json");
            if (File.Exists(embeddedManifest))
            {
                return CreateCompatiblePackageInstallation(
                    backend,
                    embeddedPath,
                    ReadPackageVersion(embeddedManifest));
            }

            try
            {
                var registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(package => string.Equals(package.name, packageName, StringComparison.Ordinal));
                if (registered != null && !string.IsNullOrEmpty(registered.resolvedPath))
                {
                    return CreateCompatiblePackageInstallation(
                        backend,
                        registered.resolvedPath,
                        registered.version);
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        internal static bool IsLegacyAdaptedPipelinePackage(string projectRoot)
        {
            var packageRoot = Path.Combine(projectRoot, "Packages", PipelinePackageName);
            return IsLegacyAdaptedPipelinePackageAtPath(packageRoot);
        }

        internal static bool IsProjectSupportReady(UnityToolingBackend backend, string projectRoot)
        {
            try
            {
                var guidePath = Path.Combine(projectRoot, "UNITY-GUIDE.md");
                return ManagedGuideMatches(guidePath, backend, "active") &&
                       IsProjectSkillSetupReady(backend, projectRoot);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool IsProjectSkillSetupReady(UnityToolingBackend backend, string projectRoot)
        {
            try
            {
                var skillsRoot = Path.Combine(projectRoot, ".agents", "skills");
                if (backend == UnityToolingBackend.OfficialPipeline)
                {
                    var skillPath = Path.Combine(skillsRoot, "unity-pipeline", "SKILL.md");
                    return File.Exists(skillPath) &&
                           File.ReadAllText(skillPath).Contains(PipelineLocalExecutionInstruction);
                }

                return Directory.Exists(skillsRoot) &&
                       Directory.GetDirectories(skillsRoot, "uloop-*", SearchOption.TopDirectoryOnly)
                           .Any(path => File.Exists(Path.Combine(path, "SKILL.md")));
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static Task<UnityToolingConnection> DetectConnectionAsync(
            UnityToolingBackend backend,
            string cliPath,
            string projectRoot)
        {
            return backend == UnityToolingBackend.OfficialPipeline
                ? Task.Run(() => DetectPipelineServer(projectRoot))
                : DetectUnityCliLoopConnectionAsync(cliPath, projectRoot);
        }

        internal static string SetupStatePath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "AgentForUnity", "unity-tooling-setup.json");
        }

        internal static UnityToolingSetupState LoadSetupState(string projectRoot)
        {
            var path = SetupStatePath(projectRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonConvert.DeserializeObject<UnityToolingSetupState>(File.ReadAllText(path));
            if (state == null)
            {
                throw new InvalidDataException("Unity tooling setup state is empty.");
            }

            ParseBackend(state.backend);
            ParseSetupPhase(state.phase);
            return state;
        }

        internal static void SaveSetupState(string projectRoot, UnityToolingSetupState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            ParseBackend(state.backend);
            ParseSetupPhase(state.phase);
            var path = SetupStatePath(projectRoot);
            WriteTextAtomically(path, JsonConvert.SerializeObject(state, Formatting.Indented) + "\n");
        }

        internal static void ClearSetupState(string projectRoot)
        {
            var path = SetupStatePath(projectRoot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        internal static void UpdateManagedGuide(
            string projectRoot,
            UnityToolingBackend backend,
            string status)
        {
            if (!string.Equals(status, "pending", StringComparison.Ordinal) &&
                !string.Equals(status, "failed", StringComparison.Ordinal) &&
                !string.Equals(status, "active", StringComparison.Ordinal))
            {
                throw new ArgumentException("Unknown managed guide status: " + status, nameof(status));
            }

            var targetPath = Path.Combine(projectRoot, "UNITY-GUIDE.md");
            string existing;
            if (File.Exists(targetPath))
            {
                existing = File.ReadAllText(targetPath);
            }
            else
            {
                var templatePath = Path.Combine(ResolveAgentForUnityPackageRoot(), "UNITY-GUIDE.md");
                if (!File.Exists(templatePath))
                {
                    throw new FileNotFoundException("Agent for Unity has no UNITY-GUIDE.md template.", templatePath);
                }
                existing = File.ReadAllText(templatePath);
            }

            var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
            var section = BuildManagedGuideSection(backend, status).Replace("\n", newline);
            var beginCount = CountOccurrences(existing, GuideBeginMarker);
            var endCount = CountOccurrences(existing, GuideEndMarker);
            string updated;
            if (beginCount == 0 && endCount == 0)
            {
                updated = section + newline + newline + existing.TrimStart('\r', '\n');
            }
            else
            {
                if (beginCount != 1 || endCount != 1)
                {
                    throw new InvalidDataException(
                        "UNITY-GUIDE.md has duplicate or incomplete Agent for Unity tooling markers.");
                }

                var begin = existing.IndexOf(GuideBeginMarker, StringComparison.Ordinal);
                var end = existing.IndexOf(GuideEndMarker, StringComparison.Ordinal);
                if (begin < 0 || end < begin)
                {
                    throw new InvalidDataException("UNITY-GUIDE.md has invalid Agent for Unity tooling marker order.");
                }

                end += GuideEndMarker.Length;
                updated = existing.Substring(0, begin) + section + existing.Substring(end);
            }

            WriteTextAtomically(targetPath, updated);
        }

        private static ToolingCliInstallation DetectCli(UnityToolingBackend backend)
        {
            var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var executableName = backend == UnityToolingBackend.OfficialPipeline
                ? (isWindows ? "unity.exe" : "unity")
                : (isWindows ? "uloop.exe" : "uloop");
            var configuredVariable = backend == UnityToolingBackend.OfficialPipeline
                ? "UNITY_CLI_EXECUTABLE"
                : "ULOOP_CLI_EXECUTABLE";
            var candidates = new List<string>();
            AddCandidate(candidates, Environment.GetEnvironmentVariable(configuredVariable));

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), executableName));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (backend == UnityToolingBackend.OfficialPipeline)
            {
                AddCandidate(candidates, Path.Combine(userProfile, ".unity", "bin", executableName));
                if (!isWindows)
                {
                    AddCandidate(candidates, "/opt/homebrew/bin/unity");
                    AddCandidate(candidates, "/usr/local/bin/unity");
                }
            }
            else
            {
                var configuredInstallDirectory = Environment.GetEnvironmentVariable("ULOOP_INSTALL_DIR");
                if (!string.IsNullOrWhiteSpace(configuredInstallDirectory))
                {
                    AddCandidate(candidates, Path.Combine(configuredInstallDirectory, executableName));
                }
                AddCandidate(candidates, Path.Combine(userProfile, ".local", "bin", executableName));
                if (isWindows)
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    AddCandidate(candidates, Path.Combine(localAppData, "Programs", "uloop", "bin", executableName));
                }
                else
                {
                    AddCandidate(candidates, "/opt/homebrew/bin/uloop");
                    AddCandidate(candidates, "/usr/local/bin/uloop");
                }
            }

            string unityCliLoopProbeDirectory = null;
            if (backend == UnityToolingBackend.UnityCliLoop)
            {
                try
                {
                    unityCliLoopProbeDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "AgentForUnity",
                        "CliProbe");
                    Directory.CreateDirectory(unityCliLoopProbeDirectory);
                }
                catch (Exception exception)
                {
                    return new ToolingCliInstallation(
                        null,
                        null,
                        "Could not create the uloop CLI probe directory: " + exception.Message);
                }
            }

            string firstError = null;
            foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var versionArgument = backend == UnityToolingBackend.OfficialPipeline ? "--version" : "-v";
                var workingDirectory = backend == UnityToolingBackend.UnityCliLoop
                    ? unityCliLoopProbeDirectory
                    : null;
                var result = RunProcess(candidate, versionArgument, workingDirectory, 10000);
                if (result.Success)
                {
                    var version = FirstNonEmptyLine(result.Output);
                    if (backend == UnityToolingBackend.UnityCliLoop &&
                        !IsVersionAtLeastMajor(version, UnityCliLoopMinimumMajorVersion))
                    {
                        if (firstError == null)
                        {
                            firstError = "uloop CLI 3 or newer is required.";
                        }
                        continue;
                    }

                    return new ToolingCliInstallation(candidate, version, null);
                }

                if (firstError == null)
                {
                    firstError = result.Error;
                }
            }

            var name = backend == UnityToolingBackend.OfficialPipeline ? "Unity CLI" : "uloop CLI";
            return new ToolingCliInstallation(null, null, firstError ?? name + " was not found.");
        }

        private static ToolingPackageInstallation CreateCompatiblePackageInstallation(
            UnityToolingBackend backend,
            string resolvedPath,
            string version)
        {
            if (backend == UnityToolingBackend.OfficialPipeline &&
                IsLegacyAdaptedPipelinePackageAtPath(resolvedPath))
            {
                return null;
            }
            if (backend == UnityToolingBackend.UnityCliLoop &&
                !IsVersionAtLeastMajor(version, UnityCliLoopMinimumMajorVersion))
            {
                return null;
            }

            return new ToolingPackageInstallation(resolvedPath, version);
        }

        private static bool IsLegacyAdaptedPipelinePackageAtPath(string packageRoot)
        {
            if (string.IsNullOrEmpty(packageRoot))
            {
                return false;
            }

            var manifestPath = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                return string.Equals(
                           manifest.Value<string>("name"),
                           PipelinePackageName,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           manifest.Value<string>("version"),
                           LegacyAdaptedPipelineVersion,
                           StringComparison.Ordinal) &&
                       string.Equals(manifest.Value<string>("unity"), "2022.3", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsVersionAtLeastMajor(string value, int minimumMajor)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim().TrimStart('v', 'V');
            var separator = normalized.IndexOfAny(new[] { '.', '-', '+' });
            var majorText = separator >= 0 ? normalized.Substring(0, separator) : normalized;
            return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) &&
                   major >= minimumMajor;
        }

        private static Task<ProcessResult> RunOfficialUnityCliInstallerAsync()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                const string command =
                    "$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex";
                return RunProcessAsync(
                    "powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(command),
                    null,
                    180000);
            }

            const string unixCommand =
                "curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash";
            return RunProcessAsync("/bin/bash", "-lc " + QuoteArgument(unixCommand), null, 180000);
        }

        private static Task<ProcessResult> RunUnityCliLoopInstallerAsync()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                const string command =
                    "irm https://raw.githubusercontent.com/hatayama/unity-cli-loop/main/scripts/install.ps1 | iex";
                return RunProcessAsync(
                    "powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(command),
                    null,
                    180000);
            }

            const string unixCommand =
                "curl -fsSL https://raw.githubusercontent.com/hatayama/unity-cli-loop/main/scripts/install.sh | sh";
            return RunProcessAsync("/bin/bash", "-lc " + QuoteArgument(unixCommand), null, 180000);
        }

        private static async Task<UnityToolingConnection> DetectUnityCliLoopConnectionAsync(
            string cliPath,
            string projectRoot)
        {
            if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath))
            {
                return new UnityToolingConnection(false, null, "Unavailable · uloop CLI missing");
            }

            var result = await RunProcessAsync(cliPath, "list --names", projectRoot, 15000);
            if (!result.Success)
            {
                return new UnityToolingConnection(false, cliPath, "Unreachable · " + result.Error);
            }

            var toolCount = result.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(line => !string.IsNullOrWhiteSpace(line));
            return new UnityToolingConnection(
                true,
                cliPath,
                "Reachable · " + toolCount + " commands");
        }

        private static UnityToolingConnection DetectPipelineServer(string projectRoot)
        {
            var descriptorPath = Path.Combine(projectRoot, "Library", "Pipeline", ".unity-pipeline-port");
            if (!File.Exists(descriptorPath))
            {
                return new UnityToolingConnection(false, null, "Unavailable · Instance descriptor missing");
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
                return new UnityToolingConnection(false, null, "Unavailable · Invalid instance descriptor");
            }

            if (port <= 0 || port > ushort.MaxValue || string.IsNullOrWhiteSpace(token))
            {
                return new UnityToolingConnection(false, null, "Unavailable · Invalid instance descriptor");
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
                        return new UnityToolingConnection(true, endpoint, "Reachable · 127.0.0.1:" + port);
                    }

                    return new UnityToolingConnection(
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
                        return new UnityToolingConnection(false, endpoint, "Unreachable · Authentication failed");
                    }
                    if (response != null)
                    {
                        return new UnityToolingConnection(
                            false,
                            endpoint,
                            "Unreachable · HTTP " + (int)response.StatusCode);
                    }
                }

                return new UnityToolingConnection(false, endpoint, "Unreachable · " + exception.Status);
            }
            catch (Exception exception)
            {
                return new UnityToolingConnection(false, endpoint, "Unreachable · " + exception.Message);
            }
        }

        private static bool ManagedGuideMatches(
            string path,
            UnityToolingBackend backend,
            string status)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            if (CountOccurrences(text, GuideBeginMarker) != 1 ||
                CountOccurrences(text, GuideEndMarker) != 1)
            {
                return false;
            }

            var begin = text.IndexOf(GuideBeginMarker, StringComparison.Ordinal);
            var end = text.IndexOf(GuideEndMarker, StringComparison.Ordinal);
            if (begin < 0 || end <= begin)
            {
                return false;
            }

            var managed = text.Substring(begin, end - begin);
            return managed.Contains("**Tooling status: `" + status + "`**") &&
                   managed.Contains("**Active backend: `" + BackendToken(backend) + "`");
        }

        private static string BuildManagedGuideSection(UnityToolingBackend backend, string status)
        {
            var builder = new StringBuilder();
            builder.AppendLine(GuideBeginMarker);
            builder.AppendLine("**Tooling status: `" + status + "`**");
            builder.AppendLine();
            if (!string.Equals(status, "active", StringComparison.Ordinal))
            {
                builder.AppendLine("**Requested backend: `" + BackendToken(backend) + "`**");
                builder.AppendLine();
                builder.AppendLine("- This managed section overrides every conflicting Unity Editor tooling instruction elsewhere in this file, including any later rule that says Pipeline or uloop is required.");
                builder.AppendLine("- Do not use any Unity Editor bridge while tooling setup or a backend switch is pending or failed.");
                builder.AppendLine("- Installed CLIs, packages, servers, and skills from earlier setups remain inactive. Do not use Unity CLI/Pipeline, Unity CLI Loop, Unity MCP, or related Editor-control skills.");
            }
            else if (backend == UnityToolingBackend.OfficialPipeline)
            {
                builder.AppendLine("**Active backend: `" + OfficialPipelineToken + "` (Unity CLI + Pipeline)**");
                builder.AppendLine();
                builder.AppendLine("- This managed section overrides every conflicting Unity Editor tooling instruction elsewhere in this file. Any later unqualified Pipeline or uloop requirement is legacy and cannot activate another backend.");
                builder.AppendLine("- Use only the official Unity CLI, `com.unity.pipeline`, and the installed `unity-pipeline` skill for Unity Editor operations.");
                builder.AppendLine("- Do not use `uloop`, Unity CLI Loop skills, Unity MCP, or any other Editor bridge.");
                builder.AppendLine("- For localhost `unity pipeline` or `unity command` calls, request the approved local execution context on the first attempt.");
            }
            else
            {
                builder.AppendLine("**Active backend: `" + UnityCliLoopToken + "` (Unity CLI Loop)**");
                builder.AppendLine();
                builder.AppendLine("- This managed section overrides every conflicting Unity Editor tooling instruction elsewhere in this file. Ignore later legacy text such as `Pipeline Is Required`; it cannot activate Pipeline.");
                builder.AppendLine("- Use only `uloop`, `io.github.hatayama.uloopmcp`, and the installed `uloop-*` skills for Unity Editor operations.");
                builder.AppendLine("- Do not use `unity pipeline`, `unity command`, Pipeline skills, Unity MCP, or any other Editor bridge.");
                builder.AppendLine("- Use `uloop list --names` in the Unity project root as the live capability and connectivity check.");
            }
            builder.Append(GuideEndMarker);
            return builder.ToString();
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
            WriteTextAtomically(path, text);
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

        private static bool TryParseUnityVersion(string value, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split('.');
            return parts.Length >= 2 &&
                   int.TryParse(parts[0], out major) &&
                   int.TryParse(parts[1], out minor);
        }

        private static int CountOccurrences(string value, string token)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
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

        private static void AddCandidate(ICollection<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                candidates.Add(Path.GetFullPath(path.Trim()));
            }
            catch (Exception)
            {
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

        private static void WriteTextAtomically(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            var temporaryPath = path + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, value, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
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
                        WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                            ? Environment.CurrentDirectory
                            : workingDirectory,
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
                return new ProcessResult(
                    false,
                    null,
                    string.IsNullOrWhiteSpace(error) ? "Unknown process error." : error);
            }
        }
    }
}
