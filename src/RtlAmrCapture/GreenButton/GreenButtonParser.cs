using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using RtlAmrCapture.Data;

namespace RtlAmrCapture.GreenButton
{
    /// <summary>
    /// The file is not a usable Green Button file. Retrying will not help, unlike a database error.
    /// </summary>
    public class GreenButtonFormatException : Exception
    {
        public GreenButtonFormatException(string message) : base(message) { }
        public GreenButtonFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <param name="Readings">Parsed readings, one per interval. When a file repeats an interval,
    /// the last occurrence wins.</param>
    /// <param name="SkippedReadings">IntervalReadings missing a start, duration or value.</param>
    public record GreenButtonParseResult(IReadOnlyList<UtilityReading> Readings, int SkippedReadings);

    /// <summary>
    /// Reads Green Button "Download My Data" files: the NAESB ESPI XML format, an Atom feed whose
    /// entries are UsagePoint, MeterReading, ReadingType and IntervalBlock resources tied together
    /// by their link hrefs.
    /// </summary>
    public static class GreenButtonParser
    {
        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        private static readonly XNamespace Espi = "http://naesb.org/espi";

        // ESPI unit-of-measure and reading-kind codes.
        private const int UomWatts = 38;
        private const int UomWattHours = 72;
        private const int ReadingKindDemand = 8;
        private static readonly Dictionary<int, string> UnitNames = new()
        {
            [42] = "m3",
            [119] = "ft3",
            [169] = "therm",
        };

        /// <summary>
        /// Parses a .xml file, or every .xml entry in a .zip file (utilities often zip the download).
        /// </summary>
        /// <param name="wallClockZone">Time zone for files that record local clock times as UTC
        /// (see <see cref="Parse"/>). Defaults to the server's time zone.</param>
        public static GreenButtonParseResult ParseFile(string path, string sourceName, TimeZoneInfo? wallClockZone = null)
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Parse(stream, sourceName, wallClockZone);
            }

            ZipArchive archive;
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                archive = new ZipArchive(file, ZipArchiveMode.Read);
            }
            catch (InvalidDataException ex)
            {
                file.Dispose();
                throw new GreenButtonFormatException("Not a valid zip file.", ex);
            }

            using (archive)
            {
                var xmlEntries = archive.Entries
                    .Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (xmlEntries.Count == 0)
                    throw new GreenButtonFormatException("The zip file contains no .xml files. Download the Green Button XML format, not CSV.");

                var readings = new List<UtilityReading>();
                var skipped = 0;
                foreach (var entry in xmlEntries)
                {
                    using var stream = entry.Open();
                    var result = Parse(stream, sourceName, wallClockZone);
                    readings.AddRange(result.Readings);
                    skipped += result.SkippedReadings;
                }

                return new GreenButtonParseResult(Deduplicate(readings), skipped);
            }
        }

        public static GreenButtonParseResult Parse(Stream xml, string sourceName, TimeZoneInfo? wallClockZone = null)
        {
            XDocument doc;
            try
            {
                // No DTDs: blocks entity-expansion attacks from a hostile file in the drop folder.
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(xml, settings);
                doc = XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                throw new GreenButtonFormatException("Not a well-formed XML file: " + ex.Message, ex);
            }

            // Descendants rather than feed/entry, so a file holding a single bare entry also works.
            var entries = doc.Descendants(Atom + "entry").Select(e => new Entry(e)).ToList();

            var usagePoints = IndexByType(entries, "UsagePoint");
            var meterReadings = IndexByType(entries, "MeterReading");
            var readingTypes = IndexByType(entries, "ReadingType");
            var intervalBlocks = entries.Where(e => e.ContentType == "IntervalBlock").ToList();

            if (intervalBlocks.Count == 0)
                throw new GreenButtonFormatException(
                    "No interval data found. Download the Green Button usage file, not a bill or summary.");

            // Opower, which hosts Green Button for PECO and many other utilities, has a known quirk
            // handled below. Its resource hrefs all live under /Opower/espi/.
            var isOpower = doc.Descendants(Atom + "link")
                .Any(l => ((string?)l.Attribute("href"))?.Contains("/opower/espi/", StringComparison.OrdinalIgnoreCase) == true);

            // ESPI start times are meant to be UTC, but Opower writes local clock time as if it were
            // UTC and declares a zero offset: PECO's reading for midnight Eastern is stamped
            // 00:00Z. Left alone, every hour lands 4-5 hours early. When Opower declares no
            // offset, read the times as local clock time instead. A file with a real offset is
            // taken at its word, in case Opower ever fixes this.
            var startsAreLocal = isOpower
                && doc.Descendants(Espi + "LocalTimeParameters").All(ltp => (ChildLong(ltp, "tzOffset") ?? 0) == 0);
            var zone = wallClockZone ?? TimeZoneInfo.Local;

            var readings = new List<UtilityReading>();
            var skipped = 0;
            foreach (var block in intervalBlocks)
            {
                // The hrefs are hierarchical: .../UsagePoint/1/MeterReading/2/IntervalBlock/3.
                // Walk up them to find this block's meter reading and usage point.
                var meterReadingKey = StripAfter(block.UpKey, "/intervalblock", exact: true)
                                      ?? StripAfter(block.SelfKey, "/intervalblock/", exact: false);
                var meterReading = Lookup(meterReadings, meterReadingKey);

                var readingType = meterReading?.RelatedKeys
                                      .Select(k => readingTypes.GetValueOrDefault(k))
                                      .FirstOrDefault(rt => rt != null)
                                  ?? Single(readingTypes)
                                  ?? throw new GreenButtonFormatException(
                                      "Could not match interval data to its ReadingType, so its units are unknown.");

                var usagePointKey = StripAfter(meterReadingKey ?? meterReading?.SelfKey, "/meterreading/", exact: false);
                var usagePoint = Lookup(usagePoints, usagePointKey);
                if (usagePoint == null && usagePoints.Count > 1)
                    throw new GreenButtonFormatException(
                        "Could not tell which of the file's meters (UsagePoints) some interval data belongs to.");

                var usagePointId = LastSegment(usagePoint?.SelfKey ?? usagePointKey) ?? "default";
                var rt = readingType.Content;
                var uom = ChildInt(rt, "uom");

                // Demand (power) series are not usage. Opower files repeat the hourly energy
                // values as a second "demand" series in W, which would otherwise double count.
                if (ChildInt(rt, "kind") == ReadingKindDemand || uom == UomWatts)
                    continue;

                var serviceKind = ServiceKindName(usagePoint?.Content, rt);
                var flowDirection = ChildInt(rt, "flowDirection") ?? 1;
                var multiplier = ChildInt(rt, "powerOfTenMultiplier") ?? 0;

                // Opower electric files declare Wh with a x1000 multiplier, but the values are
                // already plain Wh: 2820 is 2.82 kWh, matching the utility's own CSV export and
                // bill. Taken literally the multiplier inflates usage 1000-fold. The same
                // export's gas file (millitherms, multiplier -3) is correct, so this is narrow.
                if (isOpower && uom == UomWattHours && multiplier == 3)
                    multiplier = 0;
                var unit = uom == UomWattHours ? "kWh"
                    : uom.HasValue && UnitNames.TryGetValue(uom.Value, out var name) ? name
                    : uom.HasValue ? $"uom{uom}" : "unknown";

                foreach (var ir in block.Content.Descendants(Espi + "IntervalReading"))
                {
                    var period = ir.Element(Espi + "timePeriod");
                    var start = ChildLong(period, "start");
                    var duration = ChildInt(period, "duration");
                    var value = ChildLong(ir, "value");
                    if (start == null || duration == null || value == null)
                    {
                        skipped++;
                        continue;
                    }

                    var scaled = Scale(value.Value, multiplier);
                    if (uom == UomWattHours)
                        scaled /= 1000m;

                    // ESPI cost is in hundred-thousandths of the currency unit.
                    var cost = ChildLong(ir, "cost") is { } c ? c / 100_000m : (decimal?)null;
                    var quality = ChildInt(ir.Element(Espi + "ReadingQuality"), "quality");

                    readings.Add(new UtilityReading(
                        sourceName, usagePointId, serviceKind, flowDirection,
                        startsAreLocal ? FromLocalClock(start.Value, zone) : DateTimeOffset.FromUnixTimeSeconds(start.Value),
                        NormalizeDuration(duration.Value),
                        scaled, unit, cost, quality));
                }
            }

            return new GreenButtonParseResult(Deduplicate(readings), skipped);
        }

        /// <summary>
        /// One row per interval, last occurrence winning. Files may repeat an interval across
        /// overlapping blocks, and the database upsert rejects duplicate keys within one batch.
        /// </summary>
        public static IReadOnlyList<UtilityReading> Deduplicate(IEnumerable<UtilityReading> readings)
        {
            var byKey = new Dictionary<(string, string, string, int, DateTimeOffset, int), UtilityReading>();
            foreach (var r in readings)
                byKey[(r.SourceName, r.UsagePointId, r.ServiceKind, r.FlowDirection, r.IntervalStart, r.DurationSeconds)] = r;
            return byKey.Values.OrderBy(r => r.IntervalStart).ToList();
        }

        private static string ServiceKindName(XElement? usagePoint, XElement readingType)
        {
            // Prefer the UsagePoint's service category; fall back to the ReadingType's commodity.
            var kind = ChildInt(usagePoint?.Element(Espi + "ServiceCategory"), "kind");
            if (kind.HasValue)
                return kind.Value switch
                {
                    0 => "Electricity",
                    1 => "Gas",
                    2 => "Water",
                    _ => $"Kind{kind}",
                };

            var commodity = ChildInt(readingType, "commodity");
            return commodity switch
            {
                1 or 2 => "Electricity",
                7 => "Gas",
                9 => "Water",
                null => "Unknown",
                _ => $"Commodity{commodity}",
            };
        }

        /// <summary>
        /// Some utilities (Opower among them) give inclusive interval lengths: 3599 seconds for
        /// an hour, 899 for 15 minutes. Taken as-is, every interval would end a second before
        /// the next begins, and gap detection would report a one-second outage every hour.
        /// </summary>
        private static int NormalizeDuration(int seconds) =>
            seconds % 60 == 59 ? seconds + 1 : seconds;

        /// <summary>
        /// Converts a "local clock time written as UTC" timestamp to the real instant.
        /// </summary>
        /// <remarks>
        /// At the autumn change the 1 AM hour happens twice; this picks the first (daylight)
        /// occurrence. If a file lists both hours under the same clock time, they collapse to
        /// one row in <see cref="Deduplicate"/>, losing one hour of that day.
        /// </remarks>
        private static DateTimeOffset FromLocalClock(long start, TimeZoneInfo zone)
        {
            var clock = DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime, DateTimeKind.Unspecified);
            var offset = zone.IsAmbiguousTime(clock)
                ? zone.GetAmbiguousTimeOffsets(clock).Max()
                : zone.GetUtcOffset(clock);
            return new DateTimeOffset(clock, offset).ToUniversalTime();
        }

        private static decimal Scale(long value, int powerOfTen)
        {
            decimal result = value;
            for (var i = 0; i < powerOfTen; i++) result *= 10m;
            for (var i = 0; i > powerOfTen; i--) result /= 10m;
            return result;
        }

        private static Dictionary<string, Entry> IndexByType(List<Entry> entries, string type)
        {
            var index = new Dictionary<string, Entry>();
            foreach (var e in entries.Where(e => e.ContentType == type && e.SelfKey != null))
                index[e.SelfKey!] = e;
            return index;
        }

        /// <summary>Match by key, or take the only candidate when the hrefs don't line up.</summary>
        private static Entry? Lookup(Dictionary<string, Entry> index, string? key) =>
            key != null && index.TryGetValue(key, out var e) ? e : Single(index);

        private static Entry? Single(Dictionary<string, Entry> index) =>
            index.Count == 1 ? index.Values.First() : null;

        /// <summary>
        /// Cuts <paramref name="key"/> at the last <paramref name="marker"/>. With
        /// <paramref name="exact"/>, the marker must end the key (an "up" link to a collection).
        /// </summary>
        private static string? StripAfter(string? key, string marker, bool exact)
        {
            if (key == null) return null;
            if (exact) return key.EndsWith(marker) ? key[..^marker.Length] : null;
            var i = key.LastIndexOf(marker, StringComparison.Ordinal);
            return i > 0 ? key[..i] : null;
        }

        private static string? LastSegment(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var i = key.LastIndexOf('/');
            return i >= 0 ? key[(i + 1)..] : key;
        }

        /// <summary>
        /// Normalizes a resource href to the path from its UsagePoint or ReadingType segment on,
        /// lower-cased. Hosts and prefixes (/espi/1_1/resource/Subscription/x/...) vary between
        /// exports and between links in the same file; the resource path is what identifies it.
        /// </summary>
        private static string? ResourceKey(string? href)
        {
            if (string.IsNullOrWhiteSpace(href)) return null;
            var path = href.Split('?', '#')[0].TrimEnd('/').ToLowerInvariant();
            foreach (var marker in new[] { "/usagepoint/", "/readingtype/" })
            {
                var i = path.IndexOf(marker, StringComparison.Ordinal);
                if (i >= 0) return path[(i + 1)..];
            }
            return path;
        }

        private static long? ChildLong(XElement? parent, string name) =>
            long.TryParse(parent?.Element(Espi + name)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : null;

        private static int? ChildInt(XElement? parent, string name) =>
            int.TryParse(parent?.Element(Espi + name)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : null;

        private sealed class Entry
        {
            public string? SelfKey { get; }
            public string? UpKey { get; }
            public List<string> RelatedKeys { get; }
            public string? ContentType { get; }
            public XElement Content { get; }

            public Entry(XElement entry)
            {
                string? Link(string rel) => entry.Elements(Atom + "link")
                    .FirstOrDefault(l => (string?)l.Attribute("rel") == rel)?.Attribute("href")?.Value;

                SelfKey = ResourceKey(Link("self"));
                UpKey = ResourceKey(Link("up"));
                RelatedKeys = entry.Elements(Atom + "link")
                    .Where(l => (string?)l.Attribute("rel") == "related")
                    .Select(l => ResourceKey(l.Attribute("href")?.Value))
                    .OfType<string>()
                    .ToList();
                Content = entry.Element(Atom + "content")?.Elements().FirstOrDefault(e => e.Name.Namespace == Espi)
                          ?? new XElement(Espi + "none");
                ContentType = Content.Name.LocalName == "none" ? null : Content.Name.LocalName;
            }
        }
    }
}
