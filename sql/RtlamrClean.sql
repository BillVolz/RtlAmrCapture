-- ============================================================================
-- RtlamrClean: a filtered view over RtlamrRaw
--
-- Two classes of bad data show up when capturing SCM+ traffic, and both are
-- worth removing before charting anything:
--
--   1. Phantom endpoints. rtlamr decodes every meter in radio range, and RF
--      noise occasionally produces a packet that passes SCM+'s weak checksum
--      but is not a real meter. These appear as EndpointIds seen once or twice
--      and never again. On a typical install the raw table may hold 200+
--      distinct EndpointIds while only 15-20 are genuine.
--
--   2. Bit-flip readings. An RF error can flip a high-order bit of the
--      consumption field, adding a power of two to an otherwise valid reading.
--      Offsets from 2^17 up to 2^30 have been observed in practice. A meter
--      normally reading ~20,000 gallons suddenly reports ~285,000,000, then
--      returns to normal on the next packet.
--
--      This matters most on day-over-day delta charts: the corrupted value
--      becomes that day's MAX, so the chart spikes up one day and down by the
--      same amount the next. On one install, daily deltas ranged from
--      -570,695,668 to +570,695,688 before filtering, and 0 to 198 after.
--
-- The Consumption < AvgC * 5 bound is safe against meter growth. These are
-- cumulative odometers, and for a steadily incrementing counter the current
-- reading tends toward twice its lifetime average, never approaching five
-- times it. The average also rises as the meter advances, so the bound moves
-- with it.
--
-- Tuning: raise the HAVING COUNT(*) threshold if you still see phantom
-- endpoints, or lower it if a genuine but distant meter is being excluded.
-- ============================================================================

CREATE OR ALTER VIEW dbo.RtlamrClean AS
SELECT r.*
FROM dbo.RtlamrRaw r
JOIN (
    SELECT EndpointId, AVG(CAST(Consumption AS FLOAT)) AS AvgC
    FROM dbo.RtlamrRaw
    GROUP BY EndpointId
    HAVING COUNT(*) > 1000
) m ON m.EndpointId = r.EndpointId
WHERE r.Consumption < m.AvgC * 5;
GO


-- ============================================================================
-- Homes (optional): friendly names for meters you can identify.
--
-- The Grafana dashboard LEFT JOINs this, so it works whether or not you
-- populate it. Meters with no row here display as "Meter <EndpointId>".
--
-- Finding your own meter: run the dashboard, use a lot of water, and watch
-- which EndpointId's consumption climbs.
-- ============================================================================

IF OBJECT_ID(N'[dbo].[Homes]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Homes](
        [idHomes]          [int] IDENTITY(1,1) NOT NULL,
        [Name]             [nvarchar](255) NOT NULL,
        [RtlamrEndpointId] [int] NOT NULL,
        CONSTRAINT [PK_Homes] PRIMARY KEY CLUSTERED ([idHomes] ASC)
    );

    CREATE UNIQUE INDEX [IX_Homes_RtlamrEndpointId]
        ON [dbo].[Homes] ([RtlamrEndpointId] ASC);
END
GO

-- Example:
-- INSERT INTO dbo.Homes ([Name], [RtlamrEndpointId]) VALUES ('My House', 12345678);
