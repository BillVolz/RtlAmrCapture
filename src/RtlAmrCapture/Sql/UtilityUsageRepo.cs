using System.Data;
using System.Data.SqlClient;
using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;
using RtlAmrCapture.Data;

namespace RtlAmrCapture.Sql
{
    /// <summary>
    /// Stores Green Button interval data in dbo.UtilityUsage, in every configured database.
    /// </summary>
    public class UtilityUsageRepo
    {
        private readonly ServiceConfiguration _serviceConfiguration;
        private readonly IConfiguration _configuration;
        private readonly ILogger<UtilityUsageRepo> _logger;

        public UtilityUsageRepo(IOptions<ServiceConfiguration> serviceConfiguration, IConfiguration configuration,
            ILogger<UtilityUsageRepo> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _serviceConfiguration = serviceConfiguration.Value;
        }

        public async Task TryCreateTable(CancellationToken cancellationToken)
        {
            foreach (var (name, connectionString) in GetConnectionStrings())
            {
                await using var c = new SqlConnection(connectionString);
                await c.OpenAsync(cancellationToken);
                foreach (var sql in new[] { CreateTableSql, CreateViewSql })
                {
                    await using var cmd = c.CreateCommand();
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                _logger.LogDebug("UtilityUsage table ready in {connectionStringName}.", name);
            }
        }

        /// <summary>
        /// Inserts new intervals and updates ones already stored, so re-importing an overlapping
        /// download is safe. Returns false if any database could not be written; the caller keeps
        /// the file and retries it later, which is harmless for the databases that did succeed.
        /// </summary>
        public async Task<bool> Upsert(IReadOnlyList<UtilityReading> readings, CancellationToken cancellationToken)
        {
            var allSucceeded = true;
            foreach (var (name, connectionString) in GetConnectionStrings())
            {
                try
                {
                    var (inserted, updated) = await UpsertOne(connectionString, readings, cancellationToken);
                    _logger.LogInformation(
                        "Green Button import to {connectionStringName}: {inserted} new intervals, {updated} updated.",
                        name, inserted, updated);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    _logger.LogError(ex, "Green Button import to {connectionStringName} failed.", name);
                }
            }
            return allSucceeded;
        }

        private async Task<(int inserted, int updated)> UpsertOne(string connectionString,
            IReadOnlyList<UtilityReading> readings, CancellationToken cancellationToken)
        {
            var timeout = _serviceConfiguration.SqlCommandTimeoutSeconds > 0
                ? _serviceConfiguration.SqlCommandTimeoutSeconds
                : DefaultCommandTimeoutSeconds;

            await using var c = new SqlConnection(connectionString);
            await c.OpenAsync(cancellationToken);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(cancellationToken);

            await using (var create = c.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = CreateStagingSql;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx))
            {
                bulk.DestinationTableName = "#UtilityUsageStaging";
                bulk.BulkCopyTimeout = timeout;
                await bulk.WriteToServerAsync(ToDataTable(readings), cancellationToken);
            }

            int inserted = 0, updated = 0;
            await using (var merge = c.CreateCommand())
            {
                merge.Transaction = tx;
                merge.CommandText = MergeSql;
                merge.CommandTimeout = timeout;
                await using var reader = await merge.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(0) == "INSERT") inserted++;
                    else updated++;
                }
            }

            await tx.CommitAsync(cancellationToken);
            return (inserted, updated);
        }

        private static DataTable ToDataTable(IReadOnlyList<UtilityReading> readings)
        {
            var table = new DataTable();
            table.Columns.Add("SourceName", typeof(string));
            table.Columns.Add("UsagePointId", typeof(string));
            table.Columns.Add("ServiceKind", typeof(string));
            table.Columns.Add("FlowDirection", typeof(int));
            table.Columns.Add("IntervalStart", typeof(DateTimeOffset));
            table.Columns.Add("DurationSeconds", typeof(int));
            table.Columns.Add("Value", typeof(decimal));
            table.Columns.Add("Unit", typeof(string));
            table.Columns.Add("Cost", typeof(decimal));
            table.Columns.Add("ReadingQuality", typeof(int));

            foreach (var r in readings)
            {
                table.Rows.Add(r.SourceName, r.UsagePointId, r.ServiceKind, r.FlowDirection, r.IntervalStart,
                    r.DurationSeconds, r.Value, r.Unit, (object?)r.Cost ?? DBNull.Value,
                    (object?)r.ReadingQuality ?? DBNull.Value);
            }
            return table;
        }

        private IEnumerable<(string name, string connectionString)> GetConnectionStrings()
        {
            if (_serviceConfiguration.Connections == null)
            {
                _logger.LogError("No connection string defined.");
                yield break;
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

                yield return (conn.ConnectionStringName, connectionString);
            }
        }

        /// <summary>Used when SqlCommandTimeoutSeconds is configured to a non-positive value.</summary>
        private const int DefaultCommandTimeoutSeconds = 30;

        private const string CreateTableSql = @"IF OBJECT_ID(N'[dbo].[UtilityUsage]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[UtilityUsage](
                    [UtilityUsageId]  [bigint] IDENTITY(1,1) NOT NULL,
                    [SourceName]      [nvarchar](100) NOT NULL,
                    [UsagePointId]    [nvarchar](255) NOT NULL,
                    [ServiceKind]     [nvarchar](32) NOT NULL,
                    [FlowDirection]   [int] NOT NULL,
                    [IntervalStart]   [datetimeoffset](0) NOT NULL,
                    [DurationSeconds] [int] NOT NULL,
                    [Value]           [decimal](19, 6) NOT NULL,
                    [Unit]            [nvarchar](16) NOT NULL,
                    [Cost]            [decimal](19, 5) NULL,
                    [ReadingQuality]  [int] NULL,
                    [ImportedAt]      [datetimeoffset](0) NOT NULL,
                    CONSTRAINT [PK_UtilityUsage] PRIMARY KEY NONCLUSTERED ([UtilityUsageId] ASC)
                );

                CREATE UNIQUE CLUSTERED INDEX [UX_UtilityUsage_Interval] ON [dbo].[UtilityUsage]
                (
                    [SourceName] ASC,
                    [UsagePointId] ASC,
                    [ServiceKind] ASC,
                    [FlowDirection] ASC,
                    [IntervalStart] ASC,
                    [DurationSeconds] ASC
                );
            END;";

        // Some utilities put daily totals and hourly intervals in the same file. Summing both would
        // double count, so the view keeps only the finest interval length each meter reports.
        // CREATE VIEW must be the only statement in its batch, hence EXEC.
        private const string CreateViewSql = @"IF OBJECT_ID(N'[dbo].[UtilityUsageIntervals]', N'V') IS NULL
            EXEC(N'CREATE VIEW [dbo].[UtilityUsageIntervals] AS
                SELECT SourceName, UsagePointId, ServiceKind, FlowDirection, IntervalStart,
                       DurationSeconds, [Value], Unit, Cost, ReadingQuality
                FROM (
                    SELECT *, MIN(DurationSeconds) OVER (
                        PARTITION BY SourceName, UsagePointId, ServiceKind, FlowDirection) AS FinestDuration
                    FROM [dbo].[UtilityUsage]
                ) u
                WHERE DurationSeconds = FinestDuration;');";

        private const string CreateStagingSql = @"CREATE TABLE #UtilityUsageStaging(
                [SourceName]      [nvarchar](100) NOT NULL,
                [UsagePointId]    [nvarchar](255) NOT NULL,
                [ServiceKind]     [nvarchar](32) NOT NULL,
                [FlowDirection]   [int] NOT NULL,
                [IntervalStart]   [datetimeoffset](0) NOT NULL,
                [DurationSeconds] [int] NOT NULL,
                [Value]           [decimal](19, 6) NOT NULL,
                [Unit]            [nvarchar](16) NOT NULL,
                [Cost]            [decimal](19, 5) NULL,
                [ReadingQuality]  [int] NULL);";

        private const string MergeSql = @"MERGE [dbo].[UtilityUsage] WITH (HOLDLOCK) AS t
            USING #UtilityUsageStaging AS s
               ON t.SourceName = s.SourceName
              AND t.UsagePointId = s.UsagePointId
              AND t.ServiceKind = s.ServiceKind
              AND t.FlowDirection = s.FlowDirection
              AND t.IntervalStart = s.IntervalStart
              AND t.DurationSeconds = s.DurationSeconds
            WHEN MATCHED THEN UPDATE SET
                 [Value] = s.[Value], Unit = s.Unit, Cost = s.Cost,
                 ReadingQuality = s.ReadingQuality, ImportedAt = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN INSERT
                 (SourceName, UsagePointId, ServiceKind, FlowDirection, IntervalStart, DurationSeconds,
                  [Value], Unit, Cost, ReadingQuality, ImportedAt)
                 VALUES (s.SourceName, s.UsagePointId, s.ServiceKind, s.FlowDirection, s.IntervalStart,
                  s.DurationSeconds, s.[Value], s.Unit, s.Cost, s.ReadingQuality, SYSDATETIMEOFFSET())
            OUTPUT $action;";
    }
}
