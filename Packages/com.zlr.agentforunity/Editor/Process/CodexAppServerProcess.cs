using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentForUnity.Editor.Codex
{
    internal interface ICodexAppServerTransport : IDisposable
    {
        bool HasExited { get; }
        int ExitCode { get; }
        bool TryReadStdout(out string line);
        bool TryReadStderr(out string line);
        Task WriteLineAsync(string line);
    }

    internal sealed class CodexAppServerProcess : ICodexAppServerTransport
    {
        private readonly ConcurrentQueue<string> _stdoutLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _stderrLines = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _stdinLock = new SemaphoreSlim(1, 1);
        private System.Diagnostics.Process _process;
        private int _disposed;

        internal int ProcessId => _process != null ? _process.Id : 0;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process == null || _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return _process != null && _process.HasExited ? _process.ExitCode : 0;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        internal void Start(string executablePath, string workingDirectory)
        {
            if (_process != null)
            {
                throw new InvalidOperationException("The App Server process has already been started.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "app-server --listen stdio://",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _process = new System.Diagnostics.Process { StartInfo = startInfo };
            try
            {
                if (!_process.Start())
                {
                    throw new InvalidOperationException("Codex App Server did not start.");
                }

                _process.StandardInput.AutoFlush = true;
                _ = ReadLinesAsync(_process.StandardOutput, _stdoutLines);
                _ = ReadLinesAsync(_process.StandardError, _stderrLines);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool TryReadStdout(out string line)
        {
            return _stdoutLines.TryDequeue(out line);
        }

        public bool TryReadStderr(out string line)
        {
            return _stderrLines.TryDequeue(out line);
        }

        public async Task WriteLineAsync(string line)
        {
            if (Volatile.Read(ref _disposed) != 0 || _process == null || HasExited)
            {
                throw new InvalidOperationException("Codex App Server is not running.");
            }

            await _stdinLock.WaitAsync();
            try
            {
                await _process.StandardInput.WriteLineAsync(line);
                await _process.StandardInput.FlushAsync();
            }
            finally
            {
                _stdinLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var process = _process;
            _process = null;
            if (process == null)
            {
                _stdinLock.Dispose();
                return;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception)
            {
                // The child may have already closed its input pipe.
            }

            try
            {
                if (!process.HasExited && !process.WaitForExit(1500))
                {
                    // This Process instance is the exact child started by this integration.
                    process.Kill();
                    process.WaitForExit(500);
                }
            }
            catch (Exception)
            {
                // Unity shutdown must continue even if the OS has already reaped the child.
            }
            finally
            {
                process.Dispose();
                _stdinLock.Dispose();
            }
        }

        private static async Task ReadLinesAsync(StreamReader reader, ConcurrentQueue<string> destination)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    destination.Enqueue(line);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException exception)
            {
                destination.Enqueue($"I/O error: {exception.Message}");
            }
        }
    }
}
