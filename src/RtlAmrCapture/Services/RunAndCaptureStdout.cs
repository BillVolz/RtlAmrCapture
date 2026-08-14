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

        public RunAndCaptureStdout(IOptions<ServiceConfiguration> options, ILogger<RunAndCaptureStdout> logger)
        {
            _options = options;
            _logger = logger;
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
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw (new Exception($"Processed closed Code: {process.ExitCode} {await process.StandardError.ReadToEndAsync()}"));
            return process.ExitCode;
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
