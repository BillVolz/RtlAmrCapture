using System.IO.Compression;
using System.Text;
using RtlAmrCapture.GreenButton;
using Xunit;

namespace RtlAmrCapture.Tests
{
    // All data here is synthetic. Never commit a real utility download: it carries the account
    // holder's name, address and account number, and hourly usage shows when a home is empty.
    public class GreenButtonParserTests
    {
        private const string Base = "https://utility.example/DataCustodian/espi/1_1/resource/Subscription/9";

        // 2024-01-01T05:00:00Z, midnight Eastern.
        private const long T0 = 1704085200;

        [Fact]
        public void ParsesHourlyElectricWithScalingAndCost()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                IntervalBlock("1", "1", "1",
                    Reading(T0, 3600, 1234, cost: 18_500),
                    Reading(T0 + 3600, 3600, 0)));

            var result = Parse(xml);

            Assert.Equal(0, result.SkippedReadings);
            Assert.Equal(2, result.Readings.Count);
            var first = result.Readings[0];
            Assert.Equal("Home", first.SourceName);
            Assert.Equal("1", first.UsagePointId);
            Assert.Equal("Electricity", first.ServiceKind);
            Assert.Equal(1, first.FlowDirection);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T0), first.IntervalStart);
            Assert.Equal(3600, first.DurationSeconds);
            Assert.Equal(1.234m, first.Value);
            Assert.Equal("kWh", first.Unit);
            Assert.Equal(0.185m, first.Cost);

            // A reported zero stays a zero; it is not the same as a missing hour.
            Assert.Equal(0m, result.Readings[1].Value);
            Assert.Null(result.Readings[1].Cost);
        }

        [Fact]
        public void MissingIntervalsProduceNoRows()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                IntervalBlock("1", "1", "1",
                    Reading(T0, 3600, 500),
                    Reading(T0 + 3 * 3600, 3600, 700)));

            var starts = Parse(xml).Readings.Select(r => r.IntervalStart.ToUnixTimeSeconds()).ToList();

            Assert.Equal(new[] { T0, T0 + 3 * 3600 }, starts);
        }

        [Fact]
        public void AppliesPowerOfTenMultiplier()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: -3),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 1_500_000)));

            Assert.Equal(1.5m, Parse(xml).Readings.Single().Value);
        }

        [Fact]
        public void SeparatesElectricAndGasMetersInOneFile()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                UsagePoint("2", kind: 1),
                MeterReading("1", "1", readingType: "RT1"),
                MeterReading("2", "1", readingType: "RT2"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                ReadingType("RT2", commodity: 7, uom: 169, multiplier: -2),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 2000)),
                IntervalBlock("2", "1", "1", Reading(T0, 86400, 350)));

            var readings = Parse(xml).Readings;

            var electric = readings.Single(r => r.UsagePointId == "1");
            Assert.Equal("Electricity", electric.ServiceKind);
            Assert.Equal(2m, electric.Value);
            Assert.Equal("kWh", electric.Unit);

            var gas = readings.Single(r => r.UsagePointId == "2");
            Assert.Equal("Gas", gas.ServiceKind);
            Assert.Equal(3.5m, gas.Value);
            Assert.Equal("therm", gas.Unit);
            Assert.Equal(86400, gas.DurationSeconds);
        }

        [Fact]
        public void KeepsSolarExportSeparateFromDelivered()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                MeterReading("1", "2", readingType: "RT2"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0, flowDirection: 1),
                ReadingType("RT2", commodity: 1, uom: 72, multiplier: 0, flowDirection: 19),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 800)),
                IntervalBlock("1", "2", "1", Reading(T0, 3600, 300)));

            var readings = Parse(xml).Readings;

            Assert.Equal(0.8m, readings.Single(r => r.FlowDirection == 1).Value);
            Assert.Equal(0.3m, readings.Single(r => r.FlowDirection == 19).Value);
        }

        [Fact]
        public void RepeatedIntervalKeepsLastValue()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 100)),
                IntervalBlock("1", "1", "2", Reading(T0, 3600, 150)));

            Assert.Equal(0.15m, Parse(xml).Readings.Single().Value);
        }

        [Fact]
        public void FallsBackToTheOnlyReadingTypeWhenLinksDoNotMatch()
        {
            // No related link from the MeterReading to its ReadingType.
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: null),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 1000)));

            Assert.Equal(1m, Parse(xml).Readings.Single().Value);
        }

        [Fact]
        public void CountsIncompleteReadingsAsSkipped()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                IntervalBlock("1", "1", "1",
                    Reading(T0, 3600, 1000),
                    "<espi:IntervalReading><espi:timePeriod><espi:duration>3600</espi:duration></espi:timePeriod><espi:value>5</espi:value></espi:IntervalReading>"));

            var result = Parse(xml);

            Assert.Single(result.Readings);
            Assert.Equal(1, result.SkippedReadings);
        }

        [Fact]
        public void RejectsFileWithoutIntervalData()
        {
            var xml = Feed(UsagePoint("1", kind: 0));

            Assert.Throws<GreenButtonFormatException>(() => Parse(xml));
        }

        [Fact]
        public void RejectsMalformedXml()
        {
            Assert.Throws<GreenButtonFormatException>(() => Parse("<feed><entry>"));
        }

        [Fact]
        public void RejectsDtd()
        {
            var xml = "<?xml version=\"1.0\"?><!DOCTYPE feed [<!ENTITY x \"y\">]><feed xmlns=\"http://www.w3.org/2005/Atom\"/>";

            Assert.Throws<GreenButtonFormatException>(() => Parse(xml));
        }

        [Fact]
        public void ReadsXmlInsideZip()
        {
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 0),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 4200)));

            var path = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}.zip");
            try
            {
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("Electric_60_Minute.xml");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(xml);
                }

                var result = GreenButtonParser.ParseFile(path, "Home");

                Assert.Equal(4.2m, result.Readings.Single().Value);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void RejectsCorruptZip()
        {
            var path = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}.zip");
            try
            {
                File.WriteAllText(path, "not a zip");

                Assert.Throws<GreenButtonFormatException>(() => GreenButtonParser.ParseFile(path, "Home"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---- Opower (PECO and other Opower-hosted utilities) ----
        // Shaped after a real PECO download: urn:uuid resource IDs under /Opower/espi/, a
        // duplicate "demand" series in W, a x1000 multiplier on plain Wh values, 3599-second
        // hours, and local clock time written as UTC with a zero tzOffset.

        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // 2026-09-05 00:00 written as if UTC; it means midnight Eastern (EDT, UTC-4).
        private const long OpowerMidnight = 1788566400;

        [Fact]
        public void OpowerElectricIsScaledTimedAndDeduplicatedCorrectly()
        {
            var xml = OpowerFeed(
                OpowerUsagePoint("aaaa", kind: 0, meterReading: "m-demand", readingType: "rt-1"),
                OpowerUsagePoint("bbbb", kind: 0, meterReading: "m-energy", readingType: "rt-2"),
                OpowerReadingType("rt-1", commodity: 1, kind: 8, uom: 38, multiplier: 3, flowDirection: 0),
                OpowerReadingType("rt-2", commodity: 1, kind: 12, uom: 72, multiplier: 3, flowDirection: 1),
                OpowerIntervalBlock("aaaa", "m-demand", Reading(OpowerMidnight, 3599, 2820, cost: 62026)),
                OpowerIntervalBlock("bbbb", "m-energy", Reading(OpowerMidnight, 3599, 2820, cost: 62026)));

            var reading = ParseWithZone(xml, Eastern).Readings.Single();

            Assert.Equal("urn:uuid:bbbb", reading.UsagePointId);
            Assert.Equal(2.82m, reading.Value);
            Assert.Equal("kWh", reading.Unit);
            Assert.Equal(0.62026m, reading.Cost);
            Assert.Equal(3600, reading.DurationSeconds);
            Assert.Equal(new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero), reading.IntervalStart);
        }

        [Fact]
        public void OpowerGasKeepsItsCorrectMultiplier()
        {
            var xml = OpowerFeed(
                OpowerUsagePoint("cccc", kind: 1, meterReading: "m-gas", readingType: "rt-1"),
                OpowerReadingType("rt-1", commodity: 7, kind: 12, uom: 169, multiplier: -3, flowDirection: 1),
                OpowerIntervalBlock("cccc", "m-gas", Reading(OpowerMidnight, 3599, 1000)));

            var reading = ParseWithZone(xml, Eastern).Readings.Single();

            Assert.Equal("Gas", reading.ServiceKind);
            Assert.Equal(1m, reading.Value);
            Assert.Equal("therm", reading.Unit);
        }

        [Fact]
        public void OpowerWinterTimesUseStandardOffset()
        {
            // 2026-12-01 00:00 local is EST, UTC-5.
            var localMidnight = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var xml = OpowerFeed(
                OpowerUsagePoint("bbbb", kind: 0, meterReading: "m-energy", readingType: "rt-2"),
                OpowerReadingType("rt-2", commodity: 1, kind: 12, uom: 72, multiplier: 3, flowDirection: 1),
                OpowerIntervalBlock("bbbb", "m-energy", Reading(localMidnight, 3599, 1000)));

            Assert.Equal(new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero), ParseWithZone(xml, Eastern).Readings.Single().IntervalStart);
        }

        [Fact]
        public void OpowerRepeatedFallBackHourTakesDaylightOccurrence()
        {
            // 2026-11-01 01:00 local happens twice; the first is EDT, 05:00Z.
            var oneAm = new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var xml = OpowerFeed(
                OpowerUsagePoint("bbbb", kind: 0, meterReading: "m-energy", readingType: "rt-2"),
                OpowerReadingType("rt-2", commodity: 1, kind: 12, uom: 72, multiplier: 3, flowDirection: 1),
                OpowerIntervalBlock("bbbb", "m-energy", Reading(oneAm, 3599, 1000)));

            Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), ParseWithZone(xml, Eastern).Readings.Single().IntervalStart);
        }

        [Fact]
        public void OpowerWithRealOffsetIsTakenAsUtc()
        {
            var xml = OpowerFeed(tzOffset: -18000,
                OpowerUsagePoint("bbbb", kind: 0, meterReading: "m-energy", readingType: "rt-2"),
                OpowerReadingType("rt-2", commodity: 1, kind: 12, uom: 72, multiplier: 3, flowDirection: 1),
                OpowerIntervalBlock("bbbb", "m-energy", Reading(OpowerMidnight, 3599, 1000)));

            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(OpowerMidnight), ParseWithZone(xml, Eastern).Readings.Single().IntervalStart);
        }

        [Fact]
        public void NonOpowerFilesKeepMultiplierAndUtcTimes()
        {
            // Same Wh-with-x1000 combination outside Opower is taken literally: kWh x 1000 = MWh.
            var xml = Feed(
                UsagePoint("1", kind: 0),
                MeterReading("1", "1", readingType: "RT1"),
                ReadingType("RT1", commodity: 1, uom: 72, multiplier: 3),
                IntervalBlock("1", "1", "1", Reading(T0, 3600, 2)));

            var reading = ParseWithZone(xml, Eastern).Readings.Single();

            Assert.Equal(2m, reading.Value);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T0), reading.IntervalStart);
        }

        [Fact]
        public void ZipWithOnlyCsvExplainsToDownloadXml()
        {
            var path = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}.zip");
            try
            {
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using var writer = new StreamWriter(zip.CreateEntry("usage.csv").Open());
                    writer.Write("TYPE,DATE,START TIME,END TIME,USAGE (kWh),COST,NOTES");
                }

                var ex = Assert.Throws<GreenButtonFormatException>(() => GreenButtonParser.ParseFile(path, "Home"));
                Assert.Contains("not CSV", ex.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private const string OpowerBase = "https://utility.example/Opower/espi/1_1/resource";
        private const string OpowerSub = OpowerBase + "/Subscription/123";

        private static string OpowerFeed(params string[] entries) => OpowerFeed(0, entries);

        private static string OpowerFeed(long tzOffset, params string[] entries) =>
            Feed(new[]
            {
                Entry($"{OpowerBase}/LocalTimeParameters/urn:uuid:ltp", $"{OpowerBase}/LocalTimeParameters",
                    Array.Empty<string>(),
                    "<espi:LocalTimeParameters><espi:dstEndRule>B40E2000</espi:dstEndRule><espi:dstOffset>0</espi:dstOffset>" +
                    $"<espi:dstStartRule>360E2000</espi:dstStartRule><espi:tzOffset>{tzOffset}</espi:tzOffset></espi:LocalTimeParameters>"),
            }.Concat(entries).ToArray());

        private static string OpowerUsagePoint(string id, int kind, string meterReading, string readingType) =>
            Entry($"{OpowerSub}/UsagePoint/urn:uuid:{id}", $"{OpowerSub}/UsagePoint",
                new[] { $"{OpowerBase}/LocalTimeParameters/urn:uuid:ltp", $"{OpowerSub}/UsagePoint/urn:uuid:{id}/MeterReading" },
                $"<espi:UsagePoint><espi:ServiceCategory><espi:kind>{kind}</espi:kind></espi:ServiceCategory></espi:UsagePoint>")
            + Entry($"{OpowerSub}/UsagePoint/urn:uuid:{id}/MeterReading/urn:uuid:{meterReading}",
                $"{OpowerSub}/UsagePoint/urn:uuid:{id}/MeterReading",
                new[]
                {
                    $"{OpowerBase}/ReadingType/urn:uuid:{readingType}",
                    $"{OpowerSub}/UsagePoint/urn:uuid:{id}",
                    $"{OpowerSub}/UsagePoint/urn:uuid:{id}/MeterReading/urn:uuid:{meterReading}/IntervalBlock",
                },
                "<espi:MeterReading/>");

        private static string OpowerReadingType(string id, int commodity, int kind, int uom, int multiplier, int flowDirection) =>
            Entry($"{OpowerBase}/ReadingType/urn:uuid:{id}", $"{OpowerBase}/ReadingType", Array.Empty<string>(),
                "<espi:ReadingType><espi:accumulationBehaviour>4</espi:accumulationBehaviour>" +
                $"<espi:commodity>{commodity}</espi:commodity><espi:currency>840</espi:currency>" +
                $"<espi:flowDirection>{flowDirection}</espi:flowDirection><espi:intervalLength>3599</espi:intervalLength>" +
                $"<espi:kind>{kind}</espi:kind><espi:phase>0</espi:phase>" +
                $"<espi:powerOfTenMultiplier>{multiplier}</espi:powerOfTenMultiplier><espi:uom>{uom}</espi:uom></espi:ReadingType>");

        private static string OpowerIntervalBlock(string usagePoint, string meterReading, params string[] readings) =>
            Entry($"{OpowerSub}/UsagePoint/urn:uuid:{usagePoint}/MeterReading/urn:uuid:{meterReading}/IntervalBlock/urn:uuid:ib-{meterReading}",
                $"{OpowerSub}/UsagePoint/urn:uuid:{usagePoint}/MeterReading/urn:uuid:{meterReading}/IntervalBlock",
                new[] { $"{OpowerSub}/UsagePoint/urn:uuid:{usagePoint}/MeterReading/urn:uuid:{meterReading}" },
                "<espi:IntervalBlock>" + string.Concat(readings) + "</espi:IntervalBlock>");

        private static GreenButtonParseResult ParseWithZone(string xml, TimeZoneInfo zone) =>
            GreenButtonParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "Home", zone);

        private static GreenButtonParseResult Parse(string xml) =>
            GreenButtonParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "Home");

        private static string Feed(params string[] entries) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<feed xmlns=\"http://www.w3.org/2005/Atom\" xmlns:espi=\"http://naesb.org/espi\">" +
            "<id>urn:uuid:00000000-0000-0000-0000-000000000000</id><title>Synthetic</title>" +
            string.Concat(entries) + "</feed>";

        private static string Entry(string self, string? up, IEnumerable<string> related, string content) =>
            "<entry>" +
            $"<link rel=\"self\" href=\"{self}\"/>" +
            (up == null ? "" : $"<link rel=\"up\" href=\"{up}\"/>") +
            string.Concat(related.Select(r => $"<link rel=\"related\" href=\"{r}\"/>")) +
            $"<content>{content}</content></entry>";

        private static string UsagePoint(string id, int kind) =>
            Entry($"{Base}/UsagePoint/{id}", $"{Base}/UsagePoint",
                new[] { $"{Base}/UsagePoint/{id}/MeterReading" },
                $"<espi:UsagePoint><espi:ServiceCategory><espi:kind>{kind}</espi:kind></espi:ServiceCategory></espi:UsagePoint>");

        private static string MeterReading(string usagePoint, string id, string? readingType) =>
            Entry($"{Base}/UsagePoint/{usagePoint}/MeterReading/{id}", $"{Base}/UsagePoint/{usagePoint}/MeterReading",
                readingType == null
                    ? new[] { $"{Base}/UsagePoint/{usagePoint}/MeterReading/{id}/IntervalBlock" }
                    : new[] { $"{Base}/UsagePoint/{usagePoint}/MeterReading/{id}/IntervalBlock",
                              $"https://utility.example/DataCustodian/espi/1_1/resource/ReadingType/{readingType}" },
                "<espi:MeterReading/>");

        private static string ReadingType(string id, int commodity, int uom, int multiplier, int flowDirection = 1) =>
            Entry($"https://utility.example/DataCustodian/espi/1_1/resource/ReadingType/{id}",
                "https://utility.example/DataCustodian/espi/1_1/resource/ReadingType",
                Array.Empty<string>(),
                "<espi:ReadingType>" +
                "<espi:accumulationBehaviour>4</espi:accumulationBehaviour>" +
                $"<espi:commodity>{commodity}</espi:commodity>" +
                "<espi:currency>840</espi:currency>" +
                $"<espi:flowDirection>{flowDirection}</espi:flowDirection>" +
                "<espi:intervalLength>3600</espi:intervalLength>" +
                "<espi:kind>12</espi:kind>" +
                $"<espi:powerOfTenMultiplier>{multiplier}</espi:powerOfTenMultiplier>" +
                $"<espi:uom>{uom}</espi:uom>" +
                "</espi:ReadingType>");

        private static string IntervalBlock(string usagePoint, string meterReading, string id, params string[] readings) =>
            Entry($"{Base}/UsagePoint/{usagePoint}/MeterReading/{meterReading}/IntervalBlock/{id}",
                $"{Base}/UsagePoint/{usagePoint}/MeterReading/{meterReading}/IntervalBlock",
                Array.Empty<string>(),
                "<espi:IntervalBlock>" + string.Concat(readings) + "</espi:IntervalBlock>");

        private static string Reading(long start, int duration, long value, long? cost = null) =>
            "<espi:IntervalReading>" +
            (cost == null ? "" : $"<espi:cost>{cost}</espi:cost>") +
            $"<espi:timePeriod><espi:duration>{duration}</espi:duration><espi:start>{start}</espi:start></espi:timePeriod>" +
            $"<espi:value>{value}</espi:value>" +
            "</espi:IntervalReading>";
    }
}
