using System.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;
using RtlAmrCapture.Data;
using RtlAmrCapture.Sql;
using Xunit;

namespace RtlAmrCapture.Tests
{
    /// <summary>
    /// Runs against a real SQL Server only when RTLAMR_TEST_SQL holds a connection string to a
    /// scratch database. Otherwise every test returns immediately.
    /// </summary>
    public class UtilityUsageRepoTests
    {
        private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("RTLAMR_TEST_SQL");
        private static readonly DateTimeOffset T0 = new(2024, 1, 1, 5, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task UpsertInsertsThenUpdatesWithoutDuplicating()
        {
            if (ConnectionString == null) return;
            var source = $"test-{Guid.NewGuid():N}"[..20];
            var repo = CreateRepo();

            try
            {
                await repo.TryCreateTable(CancellationToken.None);
                await repo.TryCreateTable(CancellationToken.None); // idempotent

                Assert.True(await repo.Upsert(new[]
                {
                    Reading(source, T0, 3600, 1.5m, 0.2m),
                    Reading(source, T0.AddHours(1), 3600, 0m, null),
                }, CancellationToken.None));

                // Overlapping re-import: one changed hour, one new hour.
                Assert.True(await repo.Upsert(new[]
                {
                    Reading(source, T0.AddHours(1), 3600, 0.75m, 0.1m),
                    Reading(source, T0.AddHours(2), 3600, 2m, null),
                }, CancellationToken.None));

                var rows = await Query(source, "UtilityUsage");
                Assert.Equal(new[] { 1.5m, 0.75m, 2m }, rows.Select(r => r.value));
                Assert.Equal(new decimal?[] { 0.2m, 0.1m, null }, rows.Select(r => r.cost));
            }
            finally
            {
                await Delete(source);
            }
        }

        [Fact]
        public async Task IntervalsViewKeepsOnlyTheFinestGranularity()
        {
            if (ConnectionString == null) return;
            var source = $"test-{Guid.NewGuid():N}"[..20];
            var repo = CreateRepo();

            try
            {
                await repo.TryCreateTable(CancellationToken.None);
                Assert.True(await repo.Upsert(new[]
                {
                    Reading(source, T0, 86400, 24m, null),
                    Reading(source, T0, 3600, 1m, null),
                    Reading(source, T0.AddHours(1), 3600, 2m, null),
                }, CancellationToken.None));

                var rows = await Query(source, "UtilityUsageIntervals");
                Assert.Equal(new[] { 1m, 2m }, rows.Select(r => r.value));
            }
            finally
            {
                await Delete(source);
            }
        }

        private static UtilityReading Reading(string source, DateTimeOffset start, int duration, decimal value, decimal? cost) =>
            new(source, "1", "Electricity", 1, start, duration, value, "kWh", cost, null);

        private static UtilityUsageRepo CreateRepo()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["ConnectionStrings:Test"] = ConnectionString! })
                .Build();
            var options = Options.Create(new ServiceConfiguration
            {
                Connections = new[] { new DataBaseConnections { ConnectionStringName = "Test", Type = "SqlServer" } },
            });
            return new UtilityUsageRepo(options, configuration, NullLogger<UtilityUsageRepo>.Instance);
        }

        private static async Task<List<(decimal value, decimal? cost)>> Query(string source, string table)
        {
            var rows = new List<(decimal, decimal?)>();
            await using var c = new SqlConnection(ConnectionString);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT [Value], Cost FROM dbo.{table} WHERE SourceName = @s ORDER BY IntervalStart, DurationSeconds";
            cmd.Parameters.AddWithValue("s", source);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetDecimal(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1)));
            return rows;
        }

        private static async Task Delete(string source)
        {
            await using var c = new SqlConnection(ConnectionString);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM dbo.UtilityUsage WHERE SourceName = @s";
            cmd.Parameters.AddWithValue("s", source);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
