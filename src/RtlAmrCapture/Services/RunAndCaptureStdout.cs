using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;

namespace RtlAmrCapture.Services
{
    public class RunAndCaptureStdout
    {
        private readonly IOptions<ServiceConfiguration> _options;
        private readonly ILogger<RunAndCaptureStdout> _logger;
        private readonly ChildProcessTracker _childProcessTracker;

        public RunAndCaptureStdout(IOptions<ServiceConfiguration> options, ILogger<RunAndCaptureStdout> logger,
            ChildProcessTracker childProcessTracker)
        {
            _options = options;
            _logger = logger;
            _childProcessTracker = childProcessTracker;
        }

        public async Task<int> CaptureApp(Func<string, CancellationToken, Task> lineCapture, CancellationToken cancellationToken)
        {
            var process = new Process();
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.Arguments = _options.Value.RtlAmrArguments;
            process.StartInfo.FileName = _options.Value.FullPathToRtlAmr;
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName(_options.Value.FullPathToRtlAmr);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data == null) return;
                // Intentionally not awaited: this is a synchronous event handler, so the work
                // is detached. InvokeSafely guarantees nothing escapes onto the thread pool --
                // an unobserved exception here terminates the process.
                _ = InvokeSafely(lineCapture, args.Data, cancellationToken);
            };
            process.Start();

            // Tie rtlamr.exe's lifetime to ours at the OS level: if this process ends for any
            // reason, Windows kills rtlamr.exe too. See ChildProcessTracker for why this exists.
            _childProcessTracker.AddProcess(process.Handle);

            process.BeginOutputReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // WaitForExitAsync(CancellationToken) does not kill the process when the token
                // is cancelled, it only stops awaiting it. Without this, rtlamr.exe would be left
                // running -- orphaned, but still holding its connection to rtl_tcp open -- and
                // the caller's next attempt would end up competing with it rather than replacing
                // it. Kill it (and anything it spawned) before the cancellation propagates.
                //
                // ChildProcessTracker is a backstop for exits this code never gets to run for
                // (a crash, Environment.Exit); this is the immediate path for the common case,
                // the hang watchdog in Worker.WatchingAndRecoverTask cancelling a stalled run.
                TryKillProcessTree(process);
                throw;
            }

            if (process.ExitCode != 0)
                throw (new Exception($"Processed closed Code: {process.ExitCode} {await process.StandardError.ReadToEndAsync()}"));
            return process.ExitCode;
        }

        private void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill {ProcessName} (pid {Pid}) after cancellation. " +
                    "It may be left running; ChildProcessTracker will still clean it up if this " +
                    "service process ends.", process.ProcessName, process.Id);
            }
        }

        /// <summary>
        /// Runs the line handler as a detached task, ensuring no exception can escape onto the
        /// thread pool. The handler is expected to do its own logging; this is the last line of
        /// defence so that a failure processing one line can never terminate the service.
        /// </summary>
        private async Task InvokeSafely(Func<string, CancellationToken, Task> lineCapture, string line,
            CancellationToken cancellationToken)
        {
            try
            {
                await lineCapture(line, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Listener is being cancelled (shutdown, or the hang watchdog restarting it).
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in line handler. Line dropped: {line}", line);
            }
        }
    }
}
