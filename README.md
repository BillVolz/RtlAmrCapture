### Purpose
A windows service that can capture readings from "smart meters" and log them to an SQL database. These "smart meters" use the 900MHz ISM band and can be read by a cheap rtl-sdr dongle.


### Requirements

- [rtl_tcp](https://github.com/rtlsdrblog/rtl-sdr-blog/releases) (rtl-sdr-blog build, V1.3.2 or later) to tune and read the rtl-sdr dongle.
- [rtlamr](https://github.com/bemasher/rtlamr) to decode the SCM+ messages from the feed.
- GoLang >=1.11 (Go build environment setup guide: http://golang.org/doc/code.html) to build rtlamr.
- [.NET 6 Runtime](https://dotnet.microsoft.com/download/dotnet/6.0), or the SDK if building from source.
- SQL Server (Developer or Express Edition).

### Usage
- Install [rtl_tcp] on a machine running the sdr-dongle. This can be installed on its own device if you configure it to allow remote connections.
- Install GO
- Install the latest rtlamr using GO
- Install MSSQL (Developer or Express Edition) and create a new database. The `RtlamrRaw` table and its index are created automatically on first start, so you only need to create the database itself.
- Install RtlAmr-Capture as a windows service using `sc.exe`:

  ```
  sc.exe create "Rtl Amr Capture" binPath= "C:\Path\To\RtlAmrCapture.exe" start= auto
  sc.exe start "Rtl Amr Capture"
  ```

- Modify the `appsettings.json` file: set `FullPathToRtlAmr` to your rtlamr executable and set the connection string for your SQL Server database. Start the service and have it capture.

To test, run rtlamr at the command line using msgtype all, to make sure your able to capture messages.

### Grafana dashboard

A ready-made dashboard lives in `grafana/water-consumption-dashboard.json`: daily use, a
neighborhood comparison, usage by time of day, and at-a-glance totals.

1. Run `sql/RtlamrClean.sql` against your database. This creates the `RtlamrClean` view the
   dashboard reads, and an optional `Homes` table for naming meters.
2. In Grafana, **Dashboards -> Import -> Upload JSON file**, then pick your SQL Server datasource.
3. Choose your meter from the **Meter** dropdown at the top.

Set **Baseline offset** to your meter's reading when you started capturing, so the running-total
chart starts near zero instead of at the lifetime meter value.

### Utility usage from Green Button downloads

Some meters can't be read over the air. PECO's electric meters, for example, use an encrypted
licensed-band network that rtlamr can't decode. Most US utilities do let you download your own
interval data from their website as a [Green Button](https://www.greenbuttondata.org/) file (an
ESPI XML format). The service can watch a folder and load those files into the same database.

1. Add one entry per account or property under `ServiceConfiguration` in `appsettings.json`:

   ```json
   "GreenButtonImports": [
     { "Name": "PECO Home", "WatchFolder": "C:\\GreenButton\\Home", "TimeZone": "Eastern Standard Time" }
   ]
   ```

   `TimeZone` is optional and defaults to the server's own. It only matters for utilities whose
   Green Button service is run by Opower (PECO and many others): their files record local clock
   time as if it were UTC, and the importer needs the meter's time zone to correct it. Use a
   Windows time zone ID.

2. Download your usage from the utility's website. Look for **Green Button** or **Download My
   Data** in the usage section of your account. Choose usage for a range of days (not bill
   totals), the **XML** format (CSV isn't supported), and the longest range offered. At PECO that's
   **My Green Button Data -> Download my data -> Export usage for a range of days -> XML**.
3. Drop the `.xml` file, or the `.zip` it came in, into the watch folder.

Within a minute the file is loaded into `dbo.UtilityUsage` and moved to a `processed` subfolder.
A file that isn't valid Green Button data moves to `failed` instead, and the log says why. If the
database can't be reached, the file stays put and is retried on the next scan.

Re-importing is safe: overlapping downloads update the intervals already stored instead of
duplicating them. So a routine is simply to download the last few weeks now and then and drop
the file in. One file can hold several meters (electric and gas, say); each is stored separately.

Opower-hosted files have a few quirks the importer corrects automatically, checked against
PECO's own CSV export of the same month: electric values carry a scaling factor 1000 times too
large, each hour also appears as a duplicate "demand" series, and hours are marked 3,599 seconds
long. Demand series are skipped for all utilities, since they're power readings rather than usage.

Keep each `Name` unchanged once you've imported data. It is part of every row's identity, so a
renamed source starts a new series instead of updating the old one.

**Dashboard:** import `grafana/utility-usage-dashboard.json` the same way as the water dashboard,
then pick the **Source** and **Service**. Set **Time zone** if you're not in US Eastern. The
**Gaps in Utility Data** table lists hours the utility has no reading for. A meter with no power
records nothing, so outages show up there.

Utilities usually publish interval data about a day late, so this is for trends and history, not
live status.

**Privacy:** these files contain your name, service address and account number, and hourly usage
shows when a home is empty. Keep the watch folders outside this repository.

### Troubleshooting

**The service restarts every minute and rtl_tcp keeps dying.** rtl_tcp builds from before
August 2023 crash with an access violation as soon as a client connects. This was fixed upstream
in [rtl-sdr-blog V1.3.2](https://github.com/rtlsdrblog/rtl-sdr-blog/releases). Update to the
[latest release](https://github.com/rtlsdrblog/rtl-sdr-blog/releases/latest) and copy the whole
release, since the core DLL was renamed from `librtlsdr.dll` to `rtlsdr.dll`.

**The service starts and stops every minute, and the log shows `Error connecting to spectrum
server`.** rtl_tcp is not listening. Run it by hand to see why: `No supported devices found`
means the SDR dongle is not attached, or is no longer bound to the WinUSB driver. Capture backs
off and retries on its own, so it recovers once rtl_tcp is running again.

**Readings occasionally spike to a huge value, then return to normal.** RF bit errors can flip a
high-order bit of the consumption field, adding a power of two to an otherwise valid reading. See
`sql/RtlamrClean.sql` for a view that filters these out.

### Configuration

All settings live under `ServiceConfiguration` in `appsettings.json`.

| Setting | Default | Description |
| --- | --- | --- |
| `FullPathToRtlAmr` | (required) | Full path to `rtlamr.exe`. |
| `RtlAmrArguments` | (required) | Arguments passed to rtlamr. Add `-server <host>:<port>` when rtl_tcp runs on another machine. |
| `HangDetectionMinutes` | 5 | Restart the listener if no reading arrives within this many minutes. |
| `RestartCountToShutdown` | 5 | Consecutive listener restarts before the service exits, letting Windows restart it. |
| `SqlCommandTimeoutSeconds` | 30 | Timeout for each SQL command. Raise it if the database is on slow or contended storage. |
| `SqlRetryCount` | 3 | Attempts per insert before the reading is logged and dropped. |
| `SqlRetryBaseDelayMs` | 200 | Base retry backoff, doubling per attempt (200ms, 400ms, 800ms). |
| `StartupFailuresBeforeExit` | 10 | Consecutive failed capture attempts before the service exits, when no reading has ever arrived. `0` retries forever instead of exiting. |
| `RestartBackoffBaseMs` | 1000 | Base delay between capture restarts, doubling per consecutive failure. Resets once a reading arrives. |
| `RestartBackoffMaxMs` | 60000 | Upper bound on the restart backoff. |
| `GreenButtonImports` | (none) | Folders to watch for Green Button downloads, each with a `Name`, a `WatchFolder` and an optional `TimeZone`. Omit to turn the importer off. |
| `GreenButtonScanIntervalSeconds` | 60 | How often to check the Green Button folders for new files. |
| `Connections` | (required) | One entry per destination database, each naming a key in `ConnectionStrings`. |

Connection strings themselves go in the standard `ConnectionStrings` section, keyed by the
`ConnectionStringName` referenced from `Connections`.

A failed insert never stops capture. The reading is retried with backoff and, if it still cannot be
written, it is logged and dropped.

### ToDo
Change data to use entity migrations and support all compatible databases.
