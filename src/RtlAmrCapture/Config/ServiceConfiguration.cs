using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RtlAmrCapture.Config
{
    public class ServiceConfiguration
    {
        public string? FullPathToRtlAmr { get; set; }
        public string? RtlAmrArguments { get; set; }

        public DataBaseConnections[]? Connections { get; set; }

        public int HangDetectionMinutes { get; set; } = 5;

        public int RestartCountToShutdown { get; set; } = 5;

        /// <summary>
        /// Timeout applied to each SQL command. The default of 30s is SqlCommand's own default;
        /// raise it if the database is on slow or contended storage.
        /// </summary>
        public int SqlCommandTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// How many times to retry a failed insert before dropping the reading.
        /// </summary>
        public int SqlRetryCount { get; set; } = 3;

        /// <summary>
        /// Base delay for retry backoff. Doubles on each attempt (200ms, 400ms, 800ms...).
        /// </summary>
        public int SqlRetryBaseDelayMs { get; set; } = 200;

        /// <summary>
        /// Consecutive failed capture attempts tolerated before the service exits, when no reading
        /// has ever been received. Exiting lets the service manager restart the process from clean
        /// state; the retries in front of it stop a dependency that is merely slow to come up from
        /// being treated as a fatal misconfiguration. Set to 0 to retry forever and never exit.
        /// </summary>
        public int StartupFailuresBeforeExit { get; set; } = 10;

        /// <summary>
        /// Base delay between capture restart attempts. Doubles per consecutive failure, up to
        /// <see cref="RestartBackoffMaxMs"/>, and resets as soon as a reading arrives.
        /// </summary>
        public int RestartBackoffBaseMs { get; set; } = 1000;

        /// <summary>
        /// Upper bound on the restart backoff, so a long outage settles into a steady retry
        /// interval instead of growing without limit.
        /// </summary>
        public int RestartBackoffMaxMs { get; set; } = 60_000;
    }

    public class DataBaseConnections
    {
        public string? ConnectionStringName { get; set; }
        public string? Type { get; set; }
    }
}
