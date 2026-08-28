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

### Troubleshooting

**The service restarts every minute and rtl_tcp keeps dying.** rtl_tcp builds from before
August 2023 crash with an access violation as soon as a client connects. This was fixed upstream
in [rtl-sdr-blog V1.3.2](https://github.com/rtlsdrblog/rtl-sdr-blog/releases). Update to the
[latest release](https://github.com/rtlsdrblog/rtl-sdr-blog/releases/latest) and copy the whole
release, since the core DLL was renamed from `librtlsdr.dll` to `rtlsdr.dll`.

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
| `Connections` | (required) | One entry per destination database, each naming a key in `ConnectionStrings`. |

Connection strings themselves go in the standard `ConnectionStrings` section, keyed by the
`ConnectionStringName` referenced from `Connections`.

A failed insert never stops capture. The reading is retried with backoff and, if it still cannot be
written, it is logged and dropped.

### ToDo
Change data to use entity migrations and support all compatible databases.
