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
    }

    public class DataBaseConnections
    {
        public string? ConnectionStringName { get; set; }
        public string? Type { get; set; }
    }
}
