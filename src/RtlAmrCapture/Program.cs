using NLog.Extensions.Logging;
using RtlAmrCapture;
using RtlAmrCapture.Config;
using RtlAmrCapture.Services;
using RtlAmrCapture.Sql;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((builderContext, config) =>
    {
        config.AddJsonFile($"appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.Development.json", optional: true, reloadOnChange: true);
    }).ConfigureLogging((hostContext, logging) =>
    {
        logging.AddNLog();
    })
    .ConfigureServices((context,services) =>
    {
        services.AddWindowsService(options =>
        {
            options.ServiceName = "Rtl Amr Capture";
        });

        services.AddHostedService<Worker>();
        services.AddSingleton<CaptureService>();
        // One Job Object for the process's lifetime so every rtlamr.exe RunAndCaptureStdout
        // spawns is tied to it, however many times ListeningTask restarts capture.
        services.AddSingleton<ChildProcessTracker>();
        services.AddSingleton<RunAndCaptureStdout>();
        services.Configure<ServiceConfiguration>(context.Configuration.GetSection("ServiceConfiguration"));
        services.AddSingleton<MsSqlDataRepo>();
        // Green Button folder import. Does nothing unless GreenButtonImports is configured.
        services.AddHostedService<GreenButtonImportWorker>();
        services.AddSingleton<UtilityUsageRepo>();
    })
    .Build();

await host.RunAsync();
