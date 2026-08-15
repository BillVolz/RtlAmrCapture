### Purpose
A windows service that can capture readings from "smart meters" and log them to an SQL database. These "smart meters" use the 900MHz ISM band and can be read by a cheap rtl-sdr dongle.


### Requirements

- [rtl_tcp](https://github.com/rtlsdrblog/rtl-sdr-blog/releases) (rtl-sdr-blog build, V1.3.2 or later) to tune and read the rtl-sdr dongle. See the note below on Windows crashes with older builds.
- [rtlamr](https://github.com/bemasher/rtlamr) to decode the SCM+ messages from the feed.
- GoLang >=1.11 (Go build environment setup guide: http://golang.org/doc/code.html) to build rtlamr.
- [.NET 6 Runtime](https://dotnet.microsoft.com/download/dotnet/6.0), or the SDK if building from source.
- SQL Server (Developer / Express / Community Edition).

### Usage
- Install [rtl_tcp] on a machine running the sdr-dongle. This can be installed on its own device if you configure it to allow remote connections.
- Install GO
- Install the latest rtlamr using GO
- Install MSSQL (Developer / Community Edition) and create a new database. The `RtlamrRaw` table and its index are created automatically on first start, so you only need to create the database itself.
- Install RtlAmr-Capture as a windows service using `sc.exe`:

  ```
  sc.exe create "Rtl Amr Capture" binPath= "C:\Path\To\RtlAmrCapture.exe" start= auto
  sc.exe start "Rtl Amr Capture"
  ```

- Modify the `appsettings.json` file: set `FullPathToRtlAmr` to your rtlamr executable and set the connection string for your SQL Server database. Start the service and have it capture.

To test, run rtlamr at the command line using msgtype all, to make sure your able to capture messages.

### rtl_tcp crashes on Windows when a client connects

Builds of rtl_tcp from before August 2023 crash with an access violation in ntdll.dll as soon as
a client (rtlamr, in this case) connects to it. rtl_tcp accepts the connection and appears to be
running, but dies within a second or two of the first client attaching, which then causes
RtlAmrCapture to fail its connection and restart repeatedly.

This was fixed upstream in [rtl-sdr-blog V1.3.2](https://github.com/rtlsdrblog/rtl-sdr-blog/releases/tag/V1.3.2)
("Fixed rtl_tcp on Windows"). If your rtl_tcp folder predates August 2023, or you are unsure,
download the [latest release](https://github.com/rtlsdrblog/rtl-sdr-blog/releases/latest) and
replace rtl_tcp.exe and its supporting DLLs.

A few things to know about the newer builds:

- The core DLL is renamed from `librtlsdr.dll` to `rtlsdr.dll`. Copy the whole release rather than
  overwriting individual files, so nothing is left pointing at the old name.
- `libusb-1.0.dll` and `libwinpthread-1.dll` are no longer required. libusb support is compiled
  directly into `rtlsdr.dll`; you can leave the old DLLs in place, they are simply unused.
- `rtlsdr.dll` also references `UsbDkHelper.dll`, an alternate USB backend. This is only loaded if
  you are actually using UsbDk, so a normal WinUSB/Zadig-driven dongle setup runs fine without that
  DLL present.
- The new build bundles `msvcr100.dll` and `pthreadVC2.dll`, so no separate Visual C++ Redistributable
  install is needed for rtl_tcp itself.

### Orphaned rtlamr.exe processes after a restart

Older versions had a bug where a restarted capture attempt could leave the previous rtlamr.exe
process running in the background instead of replacing it. This happened whenever the listener
was cancelled rather than exiting on its own: the hang watchdog (`HangDetectionMinutes`)
cancelling a stalled run, or the service process itself ending abruptly (a crash, or
`Environment.Exit`). `Process.WaitForExitAsync(CancellationToken)` does not kill the process on
cancellation, it only stops waiting for it, so the old rtlamr.exe kept running and kept its
connection to rtl_tcp open.

This mattered because rtl_tcp fans the same sample stream out to every connected client. Each
orphan left over from a failed run competed with the next run's rtlamr.exe for a usable
connection, which could itself starve the new run of data and trigger another restart, leaving
yet another orphan behind. Over time this could produce several zombie rtlamr.exe processes all
connected at once, which is generally what it looks like when the service seems to be crashing
and restarting for no reason even though rtl_tcp itself is fine.

This is fixed as of the version that added `ChildProcessTracker`: rtlamr.exe is now killed
explicitly whenever a capture attempt is cancelled, and is also added to a Windows Job Object so
that it is terminated automatically if the service process ends for any other reason. If you are
running an older build and see the service restarting frequently while rtl_tcp stays up, check
for multiple rtlamr.exe processes in Task Manager and end the extras, or update.

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
