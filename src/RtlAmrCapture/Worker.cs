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
                }
                catch (TaskCanceledException)
                {
                    // If we cancel because of a hang, we will create a new source.
                    // If the service is ending it will not get past the loop to use this.
                    _listeningTaskCancellationToken = new CancellationTokenSource();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Message}", ex.Message);
                    //First time running, we exit the service if it fails.
                    //Look at logs to trouble shoot.
                    if (_lastSample == DateTimeOffset.MinValue)
                        Environment.Exit(1);
                }
                _restartCount++;
                //Pause before retrying.
                await Task.Delay(1000, _windowsServiceCancellationToken);
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

        

        private void OnSuccessfulCapture()
        {
            //Restart the count since we had a successful capture.
            _restartCount = 0;
            _lastSample = DateTimeOffset.Now;
        }
    }
}