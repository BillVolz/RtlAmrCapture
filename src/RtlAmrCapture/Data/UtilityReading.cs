namespace RtlAmrCapture.Data
{
    /// <summary>
    /// One interval of utility-reported usage, taken from a Green Button (ESPI) file.
    /// </summary>
    /// <param name="SourceName">The configured import name. Part of the row's identity, so two
    /// accounts whose files both number their meter "1" stay apart.</param>
    /// <param name="UsagePointId">The meter/service ID from the file's UsagePoint link.</param>
    /// <param name="ServiceKind">Electricity, Gas, Water, ...</param>
    /// <param name="FlowDirection">ESPI flow direction: 1 = delivered to you, 19 = received from
    /// you (solar export).</param>
    /// <param name="IntervalStart">Start of the interval, UTC.</param>
    /// <param name="DurationSeconds">Length of the interval.</param>
    /// <param name="Value">Usage in <paramref name="Unit"/>, already scaled by the file's
    /// power-of-ten multiplier. Watt-hours are converted to kWh.</param>
    /// <param name="Unit">kWh, therm, ...</param>
    /// <param name="Cost">Interval cost in currency units, when the utility supplies it.</param>
    /// <param name="ReadingQuality">ESPI quality code for the reading (for example, estimated),
    /// when the utility supplies it.</param>
    public record UtilityReading(
        string SourceName,
        string UsagePointId,
        string ServiceKind,
        int FlowDirection,
        DateTimeOffset IntervalStart,
        int DurationSeconds,
        decimal Value,
        string Unit,
        decimal? Cost,
        int? ReadingQuality);
}
