using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;
using RtlAmrCapture.Data;
using System.Data.SqlClient;

namespace RtlAmrCapture.Sql
{
    public class MsSqlDataRepo
    {
        private readonly ServiceConfiguration _serviceConfiguration;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MsSqlDataRepo> _logger;

        public MsSqlDataRepo(IOptions<ServiceConfiguration> serviceConfiguration, IConfiguration configuration,
            ILogger<MsSqlDataRepo> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _serviceConfiguration = serviceConfiguration.Value;
        }

        public async Task TryCreateTable(CancellationToken cancellationToken)
        {
            if (_serviceConfiguration.Connections == null)
            {
                _logger.LogError("No connection string defined.");
                return;
            }

            foreach (var conn in _serviceConfiguration.Connections)
            {
                if (string.IsNullOrEmpty(conn.ConnectionStringName))
                {
                    _logger.LogError("Null or empty connection string name found.");
                    continue;
                }

                var connectionString = _configuration.GetConnectionString(conn.ConnectionStringName);
                if (connectionString == null)
                {
                    _logger.LogError("Connection string was not found {connectionStringName}",
                        conn.ConnectionStringName);
                    continue;
                }

                await using var c = new SqlConnection(connectionString);
                await c.OpenAsync(cancellationToken);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = CreateTableSql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public async Task InsertRecord(RtlAmrData rad, CancellationToken cancellationToken)
        {
            if (_serviceConfiguration.Connections == null)
            {
                _logger.LogError("No connection string defined.");
                return;
            }

            foreach (var conn in _serviceConfiguration.Connections)
            {
                if (string.IsNullOrEmpty(conn.ConnectionStringName))
                {
                    _logger.LogError("Null or empty connection string name found.");
                    continue;
                }

                var connectionString = _configuration.GetConnectionString(conn.ConnectionStringName);
                if (connectionString == null)
                {
                    _logger.LogError("Connection string was not found {connectionStringName}",
                        conn.ConnectionStringName);
                    continue;
                }

                await InsertWithRetry(connectionString, conn.ConnectionStringName, rad, cancellationToken);
            }

        }

        /// <summary>
        /// Inserts a single reading, retrying transient failures with exponential backoff.
        /// A reading that still cannot be written is logged and dropped -- losing one meter
        /// sample is always preferable to failing the capture pipeline.
        /// </summary>
        private async Task InsertWithRetry(string connectionString, string connectionStringName,
            RtlAmrData rad, CancellationToken cancellationToken)
        {
            var attempts = Math.Max(1, _serviceConfiguration.SqlRetryCount);
            var baseDelay = Math.Max(0, _serviceConfiguration.SqlRetryBaseDelayMs);

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                // Shutting down, or the watchdog cancelled the listener. Stop cleanly rather
                // than burning through retries against a token that will never be reset.
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Insert abandoned for {connectionStringName}; cancellation requested.",
                        connectionStringName);
                    return;
                }

                try
                {
                    await using var c = new SqlConnection(connectionString);
                    await c.OpenAsync(cancellationToken);
                    await using var cmd = c.CreateCommand();
                    cmd.CommandText = InsertSql;
                    cmd.CommandTimeout = _serviceConfiguration.SqlCommandTimeoutSeconds;
                    cmd.Parameters.AddRange(GetParameters(rad, cmd).ToArray());
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation came from us (service stopping, or the hang watchdog firing).
                    // Expected control flow, not a failure worth retrying or escalating.
                    _logger.LogDebug("Insert cancelled for {connectionStringName}.", connectionStringName);
                    return;
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    var delay = baseDelay * (int)Math.Pow(2, attempt - 1);
                    _logger.LogWarning(ex,
                        "Insert attempt {attempt}/{attempts} failed for {connectionStringName}. Retrying in {delay}ms.",
                        attempt, attempts, connectionStringName, delay);

                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Final attempt failed. Drop the reading rather than propagating -- see
                    // the comment in Worker.LineCapture for why an escaping exception here
                    // used to terminate the whole process.
                    _logger.LogError(ex,
                        "Insert failed after {attempts} attempts for {connectionStringName}. Dropping reading for endpoint {endpointId}.",
                        attempts, connectionStringName, rad.Message?.EndpointID);
                    return;
                }
            }
        }

        private List<SqlParameter> GetParameters(RtlAmrData rad, SqlCommand command)
        {
            var pars = new List<SqlParameter>();

            pars.Add(new SqlParameter("stamp", rad.Time));
            pars.Add(new SqlParameter("Type", rad.Type));
            pars.Add(new SqlParameter("ProtocolId", rad.Message.ProtocolID));
            pars.Add(new SqlParameter("EndpointType", rad.Message.EndpointType));
            pars.Add(new SqlParameter("EndpointId", rad.Message.EndpointID));
            pars.Add(new SqlParameter("Consumption", rad.Message.Consumption));

            return pars;
        }

        private const string InsertSql =
            @"Insert into RtlamrRaw([Timestamp],Type,ProtocolId,EndpointType,EndpointId,Consumption) 
                VALUES(@stamp,@Type,@ProtocolId,@EndpointType,@EndpointId,@Consumption)";

        private const string CreateTableSql = @"IF OBJECT_ID(N'[dbo].[RtlamrRaw]', N'U') IS NULL
                        Begin
	                        CREATE TABLE [dbo].[RtlamrRaw](
		                    [RtlamrRawId] [bigint] IDENTITY(1,1) NOT NULL,
		                    [Timestamp] [DateTimeOffset](7) NOT NULL,
		                    [Type] [nvarchar](255) NOT NULL,
		                    ProtocolId [int] NOT NULL,
		                    EndpointType [int] NOT NULL,
		                    EndpointId [int] NOT NULL,
		                    Consumption [bigint] NOT NULL);
            
			            CREATE CLUSTERED INDEX [ClusteredIndex-EndpointId-Timestamp] ON [dbo].[RtlamrRaw]
                        (
	                        [Timestamp] ASC,
	                        [EndpointId] ASC
                        )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
                    End; ";
    }
}