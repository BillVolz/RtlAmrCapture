using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;
using RtlAmrCapture.GreenButton;
using RtlAmrCapture.Sql;

namespace RtlAmrCapture.Services
{
    /// <summary>
    /// Watches the configured folders for Green Button downloads and loads them into
    /// dbo.UtilityUsage. Runs alongside radio capture and never affects it: every failure here is
    /// logged and retried on the next scan.
    /// </summary>
    public class GreenButtonImportWorker : BackgroundService
    {
        private const string ProcessedFolder = "processed";
        private const string FailedFolder = "failed";

        // Skip files written this recently; a browser or copy may still be writing them.
        private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

        private readonly ILogger<GreenButtonImportWorker> _logger;
        private readonly ServiceConfiguration _serviceConfiguration;
        private readonly UtilityUsageRepo _repo;

        public GreenButtonImportWorker(ILogger<GreenButtonImportWorker> logger,
            IOptions<ServiceConfiguration> serviceConfiguration, UtilityUsageRepo repo)
        {
            _logger = logger;
            _serviceConfiguration = serviceConfiguration.Value;
            _repo = repo;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var imports = GetValidImports();
            if (imports.Count == 0)
                return;

            var interval = TimeSpan.FromSeconds(Math.Max(5, _serviceConfiguration.GreenButtonScanIntervalSeconds));
            _logger.LogInformation("Green Button import watching {count} folder(s).", imports.Count);

            try
            {
                // The database may not be reachable yet; keep trying rather than giving up.
                while (true)
                {
                    try
                    {
                        await _repo.TryCreateTable(stoppingToken);
                        break;
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Could not create the UtilityUsage table. Retrying in {interval}.", interval);
                        await Task.Delay(interval, stoppingToken);
                    }
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var import in imports)
                    {
                        try
                        {
                            await ScanFolder(import.Config.Name!, import.Config.WatchFolder!, import.Zone, stoppingToken);
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogError(ex, "Green Button scan of {folder} failed.", import.Config.WatchFolder);
                        }
                    }

                    await Task.Delay(interval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Service stopping.
            }
        }

        private async Task ScanFolder(string sourceName, string folder, TimeZoneInfo zone, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(folder);

            var files = Directory.EnumerateFiles(folder)
                .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < SettleTime)
                    continue;

                GreenButtonParseResult result;
                try
                {
                    result = GreenButtonParser.ParseFile(file, sourceName, zone);
                }
                catch (Exception ex) when (ex is GreenButtonFormatException or InvalidDataException)
                {
                    // InvalidDataException: a corrupt entry inside an otherwise valid zip.
                    _logger.LogError("Green Button file {file} could not be read and was moved to {failed}: {reason}",
                        file, FailedFolder, ex.Message);
                    MoveTo(file, FailedFolder);
                    continue;
                }
                catch (IOException ex)
                {
                    // Most likely still locked by whatever is writing it. Try again next scan.
                    _logger.LogDebug(ex, "Green Button file {file} is not readable yet.", file);
                    continue;
                }

                if (result.SkippedReadings > 0)
                    _logger.LogWarning("Green Button file {file}: skipped {skipped} incomplete readings.",
                        file, result.SkippedReadings);

                if (!await _repo.Upsert(result.Readings, cancellationToken))
                {
                    _logger.LogWarning("Green Button file {file} left in place; the import will be retried next scan.", file);
                    continue;
                }

                var first = result.Readings.Count > 0 ? result.Readings[0].IntervalStart : (DateTimeOffset?)null;
                var last = result.Readings.Count > 0 ? result.Readings[^1].IntervalStart : (DateTimeOffset?)null;
                _logger.LogInformation("Imported Green Button file {file}: {count} intervals from {first} to {last}.",
                    file, result.Readings.Count, first, last);
                MoveTo(file, ProcessedFolder);
            }
        }

        /// <summary>
        /// Moves the file into a subfolder, prefixed with a timestamp so repeat downloads with
        /// the same name don't collide. Kept rather than deleted: it is the original copy of the data.
        /// </summary>
        private static void MoveTo(string file, string subfolder)
        {
            var dir = Path.Combine(Path.GetDirectoryName(file)!, subfolder);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(file)}");
            if (File.Exists(target))
                target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Guid.NewGuid():N}_{Path.GetFileName(file)}");
            File.Move(file, target);
        }

        private List<(GreenButtonImport Config, TimeZoneInfo Zone)> GetValidImports()
        {
            var valid = new List<(GreenButtonImport Config, TimeZoneInfo Zone)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var import in _serviceConfiguration.GreenButtonImports ?? Array.Empty<GreenButtonImport>())
            {
                if (string.IsNullOrWhiteSpace(import.Name) || string.IsNullOrWhiteSpace(import.WatchFolder))
                {
                    _logger.LogError("Green Button import skipped: both Name and WatchFolder are required.");
                    continue;
                }
                if (import.Name.Length > 100)
                {
                    _logger.LogError("Green Button import {name} skipped: Name must be 100 characters or fewer.", import.Name);
                    continue;
                }
                if (!names.Add(import.Name))
                {
                    _logger.LogError("Green Button import {name} skipped: another import already uses that Name.", import.Name);
                    continue;
                }
                var zone = TimeZoneInfo.Local;
                if (!string.IsNullOrWhiteSpace(import.TimeZone))
                {
                    try
                    {
                        zone = TimeZoneInfo.FindSystemTimeZoneById(import.TimeZone);
                    }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                    {
                        _logger.LogError("Green Button import {name} skipped: unknown TimeZone {timeZone}. Use a Windows ID such as \"Eastern Standard Time\".",
                            import.Name, import.TimeZone);
                        continue;
                    }
                }
                valid.Add((import, zone));
            }
            return valid;
        }
    }
}
