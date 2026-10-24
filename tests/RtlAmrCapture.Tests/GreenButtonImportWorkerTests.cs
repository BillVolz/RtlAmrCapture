using System.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RtlAmrCapture.Config;
using RtlAmrCapture.Services;
using RtlAmrCapture.Sql;
using Xunit;

namespace RtlAmrCapture.Tests
{
    /// <summary>
    /// End-to-end folder import. Like <see cref="UtilityUsageRepoTests"/>, runs only when
    /// RTLAMR_TEST_SQL points at a scratch database.
    /// </summary>
    public class GreenButtonImportWorkerTests
    {
        private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("RTLAMR_TEST_SQL");

        [Fact]
        public async Task ImportsGoodFilesAndSetsAsideBadOnes()
        {
            if (ConnectionString == null) return;
            var source = $"test-{Guid.NewGuid():N}"[..20];
            var folder = Path.Combine(Path.GetTempPath(), source);
            Directory.CreateDirectory(folder);

            try
            {
                var good = Path.Combine(folder, "usage.xml");
                await File.WriteAllTextAsync(good, SampleXml);
                var bad = Path.Combine(folder, "bill.xml");
                await File.WriteAllTextAsync(bad, "<not-green-button/>");
                // Age both past the settle time so the first scan takes them.
                File.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddMinutes(-1));
                File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddMinutes(-1));

                var worker = CreateWorker(source, folder);
                await worker.StartAsync(CancellationToken.None);
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (Directory.EnumerateFiles(folder).Any() && DateTime.UtcNow < deadline)
                        await Task.Delay(200);
                }
                finally
                {
                    await worker.StopAsync(CancellationToken.None);
                }

                Assert.Empty(Directory.EnumerateFiles(folder));
                Assert.Single(Directory.EnumerateFiles(Path.Combine(folder, "processed"), "*usage.xml"));
                Assert.Single(Directory.EnumerateFiles(Path.Combine(folder, "failed"), "*bill.xml"));
                Assert.Equal(2, await CountRows(source));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
                await Delete(source);
            }
        }

        private static GreenButtonImportWorker CreateWorker(string source, string folder)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["ConnectionStrings:Test"] = ConnectionString! })
                .Build();
            var options = Options.Create(new ServiceConfiguration
            {
                Connections = new[] { new DataBaseConnections { ConnectionStringName = "Test", Type = "SqlServer" } },
                GreenButtonImports = new[] { new GreenButtonImport { Name = source, WatchFolder = folder } },
            });
            var repo = new UtilityUsageRepo(options, configuration, NullLogger<UtilityUsageRepo>.Instance);
            return new GreenButtonImportWorker(NullLogger<GreenButtonImportWorker>.Instance, options, repo);
        }

        private static async Task<int> CountRows(string source)
        {
            await using var c = new SqlConnection(ConnectionString);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM dbo.UtilityUsage WHERE SourceName = @s";
            cmd.Parameters.AddWithValue("s", source);
            return (int)(await cmd.ExecuteScalarAsync())!;
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

        // Synthetic: two hourly electric readings.
        private const string SampleXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<feed xmlns=""http://www.w3.org/2005/Atom"" xmlns:espi=""http://naesb.org/espi"">
  <entry>
    <link rel=""self"" href=""https://utility.example/espi/1_1/resource/UsagePoint/1""/>
    <content><espi:UsagePoint><espi:ServiceCategory><espi:kind>0</espi:kind></espi:ServiceCategory></espi:UsagePoint></content>
  </entry>
  <entry>
    <link rel=""self"" href=""https://utility.example/espi/1_1/resource/UsagePoint/1/MeterReading/1""/>
    <link rel=""related"" href=""https://utility.example/espi/1_1/resource/ReadingType/1""/>
    <content><espi:MeterReading/></content>
  </entry>
  <entry>
    <link rel=""self"" href=""https://utility.example/espi/1_1/resource/ReadingType/1""/>
    <content><espi:ReadingType><espi:commodity>1</espi:commodity><espi:flowDirection>1</espi:flowDirection><espi:powerOfTenMultiplier>0</espi:powerOfTenMultiplier><espi:uom>72</espi:uom></espi:ReadingType></content>
  </entry>
  <entry>
    <link rel=""self"" href=""https://utility.example/espi/1_1/resource/UsagePoint/1/MeterReading/1/IntervalBlock/1""/>
    <link rel=""up"" href=""https://utility.example/espi/1_1/resource/UsagePoint/1/MeterReading/1/IntervalBlock""/>
    <content><espi:IntervalBlock>
      <espi:IntervalReading><espi:timePeriod><espi:duration>3600</espi:duration><espi:start>1704085200</espi:start></espi:timePeriod><espi:value>900</espi:value></espi:IntervalReading>
      <espi:IntervalReading><espi:timePeriod><espi:duration>3600</espi:duration><espi:start>1704088800</espi:start></espi:timePeriod><espi:value>1100</espi:value></espi:IntervalReading>
    </espi:IntervalBlock></content>
  </entry>
</feed>";
    }
}
