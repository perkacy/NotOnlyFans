using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotOnlyFans.Service;

// NotOnlyFans.Service – background part of NotOnlyFans (runs with administrator / system rights).
//
//   As a Windows service "NotOnlyFans" (normal use; installed by tools\Install-Service.ps1):
//       reads sensors every second, drives the fans and lights the user chose, and streams everything
//       to the window app over named pipes (fans go back to the BIOS whenever the service stops).
//   NotOnlyFans.Service.exe            the same in a console window (development; needs "Run as administrator")
//   NotOnlyFans.Service.exe --dump     one-off probe: write every sensor to docs\sensors-dump.txt

if (args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase)))
{
    SensorDump.Run();
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Under the Service Control Manager this runs as a Windows service (logs go to the Event Log,
// source "NotOnlyFans"); started from a console it is a normal console app (Ctrl+C stops it).
builder.Services.AddWindowsService(options => options.ServiceName = SensorWorker.ServiceName);
builder.Services.AddHostedService<SensorWorker>();

builder.Build().Run();
