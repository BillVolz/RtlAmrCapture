using System.Text.Json;
using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;
using RtlAmrCapture.Data;
using RtlAmrCapture.Services;

namespace RtlAmrCapture
{
    public class Worker : BackgroundService
    {
        private int _restartCount = 0;
        private int _consecutiveFailures = 0;
        private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
        private readonly ILogger<Worker> _logger;
        private readonly CaptureService _captureService;
        private readonly RunAndCaptureStdout _runAndCaptureStdout;
        private CancellationToken _windowsServiceCancellationToken;
        private CancellationTokenSource _listeningTaskCancellationToken;
        private IOptions<ServiceConfiguration> _serviceConfiguration;
        public Worker(ILogger<Worker> logger, 
            CaptureService captureService, 
            RunAndCaptureStdout runAndCaptureStdout,
            IOptions<ServiceConfiguration> serviceConfiguration)
        {
            _logger = logger;
            _captureService = captureService;
            _runAndCaptureStdout = runAndCaptureStdout;
            _serviceConfiguration = serviceConfiguration;
        }
        /// <summary>
        /// Function that gets called for each line read from stdout.
        /// </summary>
        /// <param name="line"></param>
        /// <param name="cancellationToken"></param>
        private async Task LineCapture(string line, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                var obj = JsonSerializer.Deserialize<RtlAmrData>(line);
                if (obj == null)
                {
                    _logger.LogError("Unable to deserialize line {line}", line);
                    return;
                }

                await _captureService.CapturePacket(obj, cancellationToken);
                OnSuccessfulCapture();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Either the service is stopping or WatchingAndRecoverTask cancelled the
                // listener after detecting a hang. Both are normal control flow: the listening
                // loop will rebuild the token source and restart capture.
                _logger.LogDebug("Packet processing cancelled while shutting down or restarting the listener.");
            }
            catch (Exception e)
            {
                // Deliberately not rethrown.
                //
                // This method runs detached from ListeningTask -- it is invoked from the
                // process stdout callback, not awaited by the loop below. It was previously
                // declared 'async void' and rethrew here, so any exception (most often a
                // TaskCanceledException from an in-flight SQL insert when the hang watchdog
                // fired) had no Task to be observed on and tore down the process instead of
                // being handled by ListeningTask's catch blocks.
                //
                // A single unwritable meter reading must never take down the capture service.
                _logger.LogError(e, "Error processing packet, dropping this reading. {line}", line);
            }
        }


        /// <summary>
        /// Main listening task to capture data.
        /// </summary>
        /// <returns></returns>
        private async Task ListeningTask()
        {
            //Loop need to restart capture on failures or hangs.
            while (!_windowsServiceCancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _runAndCaptureStdout.CaptureApp(LineCapture, _listeningTaskCancellationToken.Token);
                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    // If we cancel because of a hang, we will create a new source.
                    // If the service is ending it will not get past the loop to use this.
                    //
                    // Catches OperationCanceledException rather than TaskCanceledException
                    // (which derives from it) because the concrete type depends on when the
                    // token was cancelled. Process.WaitForExitAsync throws TaskCanceledException
                    // when cancelled mid-await, but a plain OperationCanceledException when the
                    // token was already cancelled before the call. That second case is reachable:
                    // the generic handler below does not replace the token source, so a failed
                    // run followed by the watchdog firing leaves the next iteration starting with
                    // an already-cancelled token. Catching only TaskCanceledException let that
                    // fall through to the handler below, where the watchdog had just set
                    // _lastSample to MinValue, guaranteeing Environment.Exit(1) -- the hang
                    // recovery would kill the service instead of restarting the listener.
                    _listeningTaskCancellationToken = new CancellationTokenSource();
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _logger.LogError(ex, "{Message}", ex.Message);

                    // Give up only once the failures look permanent rather than transient.
                    //
                    // This used to call Environment.Exit(1) on the *first* failure whenever no
                    // reading had ever been captured, on the reasoning that a bad configuration
                    // should fail loudly. But the same condition is hit whenever an upstream
                    // dependency is simply not up yet: rtl_tcp not listening, or the SDR dongle
                    // unplugged. That turned a recoverable outage into an endless crash loop --
                    // the process died about a second after each start and the service manager
                    // restarted it a minute later, indefinitely. Seen in practice at 148 restarts
                    // in under three hours, every one of them also raising an alert.
                    //
                    // Retrying with backoff costs nothing when the configuration really is broken:
                    // the service still exits for the service manager to restart it, just after a
                    // bounded number of attempts rather than immediately. When the dependency is
                    // merely down, capture now resumes on its own once it returns.
                    var failuresBeforeExit = _serviceConfiguration.Value.StartupFailuresBeforeExit;
                    if (_lastSample == DateTimeOffset.MinValue
                        && failuresBeforeExit > 0
                        && _consecutiveFailures >= failuresBeforeExit)
                    {
                        _logger.LogError(
                            "Capture failed {failures} consecutive times without ever receiving a reading. Exiting so the service manager can restart us.",
                            _consecutiveFailures);
                        Environment.Exit(1);
                    }
                }
                _restartCount++;
                //Pause before retrying, backing off while failures persist.
                await Task.Delay(GetRestartDelayMs(), _windowsServiceCancellationToken);
            }
        }


        
        /// <summary>
        /// Task to restart capture if data is not received.  (Needed because I was seeing hangs from the rtlamr process.)
        /// </summary>
        /// <returns></returns>
        private async Task WatchingAndRecoverTask()
        {
            while (!_windowsServiceCancellationToken.IsCancellationRequested)
            {
                //Just starting up...
                if (_lastSample == DateTimeOffset.MinValue)
                {
                    await Task.Delay(1000, _windowsServiceCancellationToken);
                    continue;
                }

                //We restarted more that 10 times.  Lets kill and have the service restarted.
                if (_restartCount > _serviceConfiguration.Value.RestartCountToShutdown)
                {
                    _logger.LogError("Restart count of {_restartCount} service to shutdown.",_restartCount);
                    Environment.Exit(1);
                }

                if (_lastSample.AddMinutes(_serviceConfiguration.Value.HangDetectionMinutes) < DateTimeOffset.Now)
                {
                    _listeningTaskCancellationToken.Cancel();
                    _lastSample = DateTimeOffset.MinValue;
                    _logger.LogWarning("Detected {minutes} minute hang.  Restarting listener.", _serviceConfiguration.Value.HangDetectionMinutes);
                }

                await Task.Delay(1000, _windowsServiceCancellationToken);
            }
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //Set the global application is running token.
            _windowsServiceCancellationToken = stoppingToken;
            
            //Initialize the database.
            await _captureService.Initialize(_windowsServiceCancellationToken);

            //Create internal cancellation token and worker tasks.
            _listeningTaskCancellationToken = new CancellationTokenSource();

            var listeningTask = ListeningTask();
            var watchingTask = WatchingAndRecoverTask();

            //Wait until the service stops.
            await Task.Delay(-1, _windowsServiceCancellationToken);

            //Now cancel the listing task.
            _listeningTaskCancellationToken.Cancel();

            //Wait for all to close and cleanup.
            await Task.WhenAll(new Task[] {listeningTask,watchingTask});

        }

        

        /// <summary>
        /// Delay before the next capture attempt. Doubles per consecutive failure so a dependency
        /// that is down -- rtl_tcp not listening, the dongle unplugged -- is retried patiently
        /// rather than in a tight loop, and drops back to the base delay as soon as a reading
        /// arrives.
        /// </summary>
        private int GetRestartDelayMs()
        {
            var baseDelay = Math.Max(0, _serviceConfiguration.Value.RestartBackoffBaseMs);
            var maxDelay = Math.Max(baseDelay, _serviceConfiguration.Value.RestartBackoffMaxMs);

            if (_consecutiveFailures <= 0)
                return baseDelay;

            // Capped integer math. An unclamped exponent overflows the shift to a negative delay
            // once it reaches 31 and makes Task.Delay throw; same guard as in
            // MsSqlDataRepo.InsertWithRetry.
            var exponent = Math.Min(_consecutiveFailures - 1, 30);
            return (int)Math.Min(maxDelay, (long)baseDelay * (1L << exponent));
        }

        private void OnSuccessfulCapture()
        {
            //Restart the count since we had a successful capture.
            _restartCount = 0;
            _consecutiveFailures = 0;
            _lastSample = DateTimeOffset.Now;
        }
    }
}