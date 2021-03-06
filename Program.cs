﻿using System;
using System.Linq;
using System.Net.Http;
using OpenHab.UniFiProxy.Model;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenHab.UniFiProxy.Logging;
using OpenHab.UniFiProxy.Clients;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OpenHab.UniFiProxy
{
    class Program
    {
        static DateTime lastReport = DateTime.MinValue;
        static IAppConfiguration _config;
        static ICounters _counters;
        static IOpenHabClient _openHabClient;
        static IUniFiClient _uniFiClient;

        static void Main(string[] args)
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            var serviceProvider = serviceCollection.BuildServiceProvider();

            _counters = serviceProvider.GetService<ICounters>();
            _config = serviceProvider.GetService<IAppConfiguration>();
            _openHabClient = serviceProvider.GetService<IOpenHabClient>();
            _uniFiClient = serviceProvider.GetService<IUniFiClient>();

            if (!Initialize().Result) { return; }

            Log.Write("");
            Log.Write(new string('=', 80));
            Log.Write("Running");
            Log.Write(new string('-', 80));

            lastReport = DateTime.Now;
            // TimerCallback tmCallback = RunJobs;
            // Timer timer = new Timer(tmCallback, null, _config.Jobs.PollInterval * 1000, _config.Jobs.PollInterval * 1000);

            System.Threading.Timer timer = null;

            timer = new System.Threading.Timer((g) =>
            {
                var start = DateTime.Now;
                RunJobs().Wait();
                var end = DateTime.Now;
                var elapsed = (end - start).TotalMilliseconds;
                _counters.LogExecution(elapsed);

                var next = start.AddSeconds(_config.Jobs.PollInterval);
                var delay = (next > end) ? (next - end).TotalMilliseconds : 0;
                // Log.Write($"Waiting {delay} ms");
                timer.Change((int)delay, Timeout.Infinite);
            }, null, 0, Timeout.Infinite);

            Log.Write("Press the enter key to stop.");
            Console.ReadLine();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            HttpClient httpClient = new HttpClient();
            services
                .AddSingleton<HttpClient>(httpClient)
                .AddSingleton<ICounters, Counters>()
                .AddSingleton<IOpenHabClient, OpenHabClient>()
                .AddSingleton<IAppConfiguration, AppConfiguration>()
                .AddSingleton<IUniFiClient, UniFiClient>()
                ;
        }

        static async Task RunJobs()
        {
            var thisRun = DateTime.Now;
            Bootstrap data = null;
            foreach (var job in _config.Jobs.Jobs)
            {
                if (job.Frequency > 0 && job.LastRun.AddSeconds(job.Frequency) <= thisRun)
                {
                    if (data == null)
                    {
                        data = await _uniFiClient.GetBootstrap();
                    }
                    switch (job.Type.ToLower())
                    {
                        case "motion":
                            _openHabClient.RunMotion(job, data);
                            break;
                        case "uptime":
                            _openHabClient.RunUptime(job, data);
                            break;
                        case "state":
                            _openHabClient.RunState(job, data);
                            break;
                        case "storage":
                            _openHabClient.RunStorage(job, data);
                            break;
                        default:
                            Log.Write($"Unknown job type: {job.Type}");
                            break;
                    }
                }
            }
            if (DateTime.Now > lastReport.AddSeconds(_config.StatsFrequency))
            {
                Log.Write(_counters.ToString());
                lastReport = DateTime.Now;
            }
        }

        private static async Task<bool> Initialize()
        {
            Log.Write("Connecting to NVR.");

            var nvrData = await _uniFiClient.GetBootstrap();

            if (nvrData == null)
            {
                Log.Write("Could not communicate with NVR");
                return false;
            }

            var totStorage = Math.Round(nvrData.nvr.storageInfo.totalSize / 1e+9, 1);
            var usedStorage = Math.Round(nvrData.nvr.storageInfo.totalSpaceUsed / 1e+9, 1);
            var usedStoragePct = Math.Round(((usedStorage * 100) / totStorage), 1);

            Log.Write("NVR Info:");

            Log.Write($"  NVR name:      {nvrData.nvr.name}");
            Log.Write($"  NVR IP:        {nvrData.nvr.host}");
            Log.Write($"  NVR MAC:       {nvrData.nvr.mac}");
            Log.Write($"  NVR firmware:  {nvrData.nvr.firmwareVersion}");
            Log.Write($"  NVR time zone: {nvrData.nvr.timezone}");
            Log.Write($"  NVR storage:   {usedStorage} used of {totStorage} gb ({usedStoragePct}%)");

            Log.Write("Cameras:" + System.Environment.NewLine + nvrData.cameras.ToStringTable(
                new string[] { "Name", "ID", "IP", "MAC address", "Type", "State", "Last motion", "Wifi" },
                c => c.name,
                c => c.id,
                c => c.host,
                c => c.mac,
                c => c.type,
                c => c.state,
                c => c.lastMotion.FromUnixTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                c => c.stats.wifiStrength
                ));

            if (_config.Jobs.Jobs.Count() == 0)
            {
                Log.Write("");
                Log.Write("Nothing to do. No jobs defined in jobs.json.");
                return false;
            }
            if (_config.Jobs.PollInterval == 0)
            {
                Log.Write("");
                Log.Write("Nothing to do. Poll interval set to zero.");
                return false;
            }

            return true;
        }

    }
}