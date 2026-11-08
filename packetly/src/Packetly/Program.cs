using System.Net;
using System.Security.Principal;
using System.Text;
using Packetly.Api;
using Packetly.Collectors;
using Packetly.Core;
using Packetly.Interop;
using Packetly.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

// Packetly — phase 2: collector + storage + REST API.
//   packetly                          smoke test (default, phase 1 behavior)
//   packetly --serve [--port 8912]    long-running monitor with HTTP API
//   common: --db <path>  --mode auto|etw|poll  --duration <s> (smoke only)

int duration = 60;
string dbPath = "packetly.db";
string mode = "auto";
int port = 8912;
bool smoke = args.Contains("--smoke");
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--db": dbPath = args[i + 1]; break;
        case "--duration": duration = int.Parse(args[i + 1]); break;
        case "--mode": mode = args[i + 1]; break;
        case "--port": port = int.Parse(args[i + 1]); break;
    }
}
bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

if (args.Contains("--install-autostart")) return InstallAutostart(ResolveDbPath(dbPath));
if (args.Contains("--uninstall-autostart")) return UninstallAutostart();

dbPath = ResolveDbPath(dbPath);
if (smoke) return RunSmoke(dbPath, duration, mode, elevated);
await RunServeAsync(dbPath, mode, port, elevated);
return 0;

// ---- desktop integration ----

/// <summary>Explicit --db paths win; the default lives in LOCALAPPDATA and is
/// seeded once from the legacy dev location next to the project.</summary>
static string ResolveDbPath(string dbPath)
{
    if (dbPath != "packetly.db") return Path.GetFullPath(dbPath);
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packetly");
    Directory.CreateDirectory(dir);
    var target = Path.Combine(dir, "packetly.db");

    // one-time migration from the pre-rename install (%LOCALAPPDATA%\GWLite)
    if (!File.Exists(target))
    {
        var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GWLite");
        var oldDb = Path.Combine(oldDir, "gwlite.db");
        if (File.Exists(oldDb))
        {
            foreach (var (src, dst) in new[]
                     {
                         ("gwlite.db", "packetly.db"),
                         ("gwlite.db-wal", "packetly.db-wal"),
                         ("gwlite.db-shm", "packetly.db-shm"),
                         ("ui-prefs.txt", "ui-prefs.txt"),
                     })
            {
                var s = Path.Combine(oldDir, src);
                var d = Path.Combine(dir, dst);
                if (File.Exists(s) && !File.Exists(d))
                    File.Copy(s, d, false);
            }
            Console.WriteLine($"migrated data from {oldDir} to {dir}");
        }
    }

    if (!File.Exists(target))
    {
        var legacy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packetly.db"));
        if (File.Exists(legacy))
        {
            foreach (var ext in new[] { "", "-wal", "-shm" })
                if (File.Exists(legacy + ext)) File.Copy(legacy + ext, target + ext, false);
            Console.WriteLine($"migrated database from {legacy} to {target}");
        }
    }
    return target;
}

static string? FindEdge()
{
    foreach (var baseDir in new[]
             {
                 Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
             })
    {
        var candidate = Path.Combine(baseDir, "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

static string WriteAppIcon()
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packetly");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "packetly.ico");
    using var bmp = new System.Drawing.Bitmap(64, 64);
    using (var g = System.Drawing.Graphics.FromImage(bmp))
    {
        AppMark.Draw(g, 64);
    }
    using var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
    using var fs = File.Create(path);
    icon.Save(fs);
    return path;
}

static string NativeUiPath()
{
    var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packetly", "packetly-ui.exe");
    if (File.Exists(local)) return local;
    var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packetly-ui", "target", "release", "packetly-ui.exe"));
    return File.Exists(dev) ? dev : local;
}

/// <summary>Opens the dashboard: the native Rust window when available, then
/// an Edge app window, then the default browser.</summary>
static void OpenDashboard(int port)
{
    var url = $"http://127.0.0.1:{port}/";
    try
    {
        if (File.Exists(NativeUiPath()))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NativeUiPath())
            {
                UseShellExecute = true,
            });
            return;
        }
        var edge = FindEdge();
        if (edge != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(edge)
            {
                Arguments = $"--app={url}",
                UseShellExecute = true,
            });
        else
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch { }
}

/// <summary>Creates Start Menu + Desktop shortcuts that open the dashboard
/// in the native window when available, else an Edge app window.</summary>
static void CreateDashboardShortcuts(int port)
{
    var iconPath = WriteAppIcon();
    // Shortcuts start the SCHEDULED TASK (elevated, no UAC prompt): this guarantees a
    // collector is running before the UI opens. A second instance while one serves
    // simply opens the dashboard (see the port-in-use guard in Main).
    // Without this, opening the shortcut after tray-Exit launched only the UI, which
    // found no API and sat empty until reboot.
    var script = "$ws = New-Object -ComObject WScript.Shell\n" +
        "$targets = @(\n" +
        "  (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Packetly Dashboard.lnk'),\n" +
        "  (Join-Path $env:APPDATA 'Microsoft\\Windows\\Start Menu\\Programs\\Packetly Dashboard.lnk')\n" +
        ")\n" +
        "foreach ($t in $targets) {\n" +
        "  $lnk = $ws.CreateShortcut($t)\n" +
        "  $lnk.TargetPath = '" + Environment.SystemDirectory + "\\schtasks.exe'\n" +
        "  $lnk.Arguments = '/run /tn Packetly'\n" +
        "  $lnk.IconLocation = '" + iconPath + ",0'\n" +
        "  $lnk.Description = 'Packetly network monitor'\n" +
        "  $lnk.Save()\n" +
        "}\n" +
        "Write-Output 'shortcuts created'\n";
    try
    {
        var bytes = Encoding.Unicode.GetBytes(script);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(bytes),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }
    catch { }
}

// ---- shared pipeline startup ----

static int RunPowerShell(string script)
{
    var bytes = Encoding.Unicode.GetBytes(script);
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell",
        Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(bytes),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    Console.WriteLine(stdout.Trim());
    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.Trim());
    return p.ExitCode;
}

static int InstallAutostart(string dbPath)
{
    var exe = Environment.ProcessPath
        ?? throw new InvalidOperationException("cannot resolve executable path");
    var dbAbs = Path.GetFullPath(dbPath);
    Console.WriteLine($"Registering scheduled task 'Packetly' (at logon, highest privileges): {exe} --serve --db {dbAbs}");
    var script = $"""
        $ErrorActionPreference = 'Stop'
        $action = New-ScheduledTaskAction -Execute '{exe}' -Argument '--serve --db "{dbAbs}"'
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $trigger.Delay = 'PT30S'
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
        Register-ScheduledTask -TaskName 'Packetly' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
        Write-Output 'task registered'
        """;
    var rc = RunPowerShell(script);
    Console.WriteLine(rc == 0
        ? "Packetly will start automatically at logon. Remove with: packetly --uninstall-autostart"
        : "failed to register the scheduled task");
    return rc;
}

static int UninstallAutostart()
{
    Console.WriteLine("Removing scheduled task 'Packetly'…");
    var rc = RunPowerShell("Unregister-ScheduledTask -TaskName 'Packetly' -Confirm:$false -ErrorAction Stop; Write-Output 'removed'");
    Console.WriteLine(rc == 0 ? "removed." : "failed to remove the task (was it installed?)");
    return rc;
}

// ---- shared pipeline startup ----

static (Db Db, TrafficRing Ring, ProcessTracker Tracker, AppRegistry Registry, TrafficWriter Writer,
         EtwNetworkCollector? Etw, PollCollector? Poll, string UsedMode)
StartPipeline(string dbPath, string mode)
{
    var db = new Db(dbPath);
    var tracker = new ProcessTracker();
    tracker.Refresh();
    var registry = new AppRegistry(db);
    var ring = new TrafficRing(300);
    var writer = new TrafficWriter(db, ring, tracker, registry);

    EtwNetworkCollector? etw = null;
    PollCollector? poll = null;
    string usedMode;
    if (mode is "auto" or "etw")
    {
        etw = new EtwNetworkCollector();
        if (etw.Start(writer.OnEvent))
        {
            usedMode = "etw (kernel-network, real-time)";
        }
        else
        {
            Log($"ETW unavailable: {etw.Error}");
            // A kernel session can linger after an unclean exit (crash/kill) and block
            // a fresh start. Stop it once and retry before falling back to polling.
            var rc = RunPowerShell("logman stop 'NT Kernel Logger' -ets 2>$null; Write-Output ok");
            Log($"lingering kernel session stop rc={rc}");
            etw.Dispose();
            etw = new EtwNetworkCollector();
            if (etw.Start(writer.OnEvent))
            {
                Log("ETW started after clearing lingering kernel session");
                usedMode = "etw (kernel-network, real-time)";
                return (db, ring, tracker, registry, writer, etw, poll, usedMode);
            }
            Log($"ETW still unavailable: {etw.Error}");
            etw.Dispose();
            etw = null;
            if (mode == "etw") throw new InvalidOperationException("ETW collector required but unavailable");
            poll = new PollCollector();
            poll.Start(writer.OnEvent);
            usedMode = "poll (no-admin fallback, TCP only)";
        }
    }
    else
    {
        poll = new PollCollector();
        poll.Start(writer.OnEvent);
        usedMode = "poll (forced)";
    }
    return (db, ring, tracker, registry, writer, etw, poll, usedMode);
}

static void StopPipeline(EtwNetworkCollector? etw, PollCollector? poll)
{
    etw?.Stop();
    poll?.Stop();
}

// ---- smoke mode (phase 1) ----

int RunSmoke(string dbPath, int duration, string mode, bool elevated)
{
    Console.WriteLine($"Packetly phase-1 smoke | db={Path.GetFullPath(dbPath)} | duration={duration}s");
    Console.WriteLine($"elevated: {elevated}");

    var (db, ring, tracker, registry, writer, etw, poll, usedMode) = StartPipeline(dbPath, mode);
    Console.WriteLine($"process tracker: {tracker.Count} processes resolved ({tracker.LastDiagnostic})");
    Console.WriteLine($"collector: {usedMode}");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
    var ct = cts.Token;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var trackerTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { tracker.Refresh(); } catch { }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    });

    var writerTask = Task.Run(() =>
    {
        while (!ct.IsCancellationRequested)
        {
            writer.FlushOnce();
            try { Task.Delay(1000, ct).Wait(ct); } catch (OperationCanceledException) { break; }
        }
        writer.FlushOnce();
    });

    var printTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            var totals = new Dictionary<int, (long In, long Out)>();
            foreach (var b in ring.RecentSeconds(5))
                foreach (var (k, v) in b.Flows)
                {
                    var (i, o) = totals.TryGetValue(k.Pid, out var t) ? t : (0L, 0L);
                    totals[k.Pid] = (i + v.In, o + v.Out);
                }
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var top = totals.OrderByDescending(kv => kv.Value.In + kv.Value.Out).Take(5)
                .Select(kv =>
                {
                    var path = tracker.GetPath(kv.Key);
                    var name = path.Length > 0 ? Path.GetFileName(path) : $"unknown:pid{kv.Key}";
                    return $"{name} {Fmt(kv.Value.In + kv.Value.Out)} ({Fmt(kv.Value.In)}in/{Fmt(kv.Value.Out)}out)";
                });
            Console.WriteLine($"[{stamp}] seen={writer.EventsSeen} last5s: {(totals.Count == 0 ? "(no traffic)" : string.Join("  |  ", top))}");
        }
    });

    try { Task.Delay(TimeSpan.FromSeconds(duration), ct).Wait(ct); } catch (OperationCanceledException) { }

    Console.WriteLine("\nstopping...");
    StopPipeline(etw, poll);
    try { writerTask.Wait(3000); } catch { }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Console.WriteLine($"\ntotal events seen: {writer.EventsSeen}");
    Console.WriteLine($"tracker: {tracker.Count} processes known ({tracker.LastDiagnostic})");
    Console.WriteLine($"traffic_1s rows: {db.RowCount("traffic_1s")}");
    Console.WriteLine("\ntop apps this run (from SQLite):");
    foreach (var (name, inB, outB) in db.TopApps("traffic_1s", 0, now + 1, 15))
        Console.WriteLine($"  {name,-50} {Fmt(inB + outB),10}  ({Fmt(inB)} in / {Fmt(outB)} out)");
    return 0;

    static string Fmt(long b) => b switch
    {
        >= 1L << 30 => $"{b / 1073741824.0:F2}GB",
        >= 1L << 20 => $"{b / 1048576.0:F2}MB",
        >= 1L << 10 => $"{b / 1024.0:F1}KB",
        _ => $"{b}B",
    };
}

// ---- serve mode (phase 2) ----

async Task<int> RunServeAsync(string dbPath, string mode, int port, bool elevated)
{
    Log($"Packetly serve starting | db={Path.GetFullPath(dbPath)} | elevated={elevated}");

    // Another instance already serving? Open its dashboard and exit BEFORE
    // touching ETW — starting a second pipeline would fight the first one
    // over the single NT Kernel Logger session.
    try
    {
        using var probe = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var status = probe.GetStringAsync($"http://127.0.0.1:{port}/api/status").Result;
        if (status.Contains("\"app\""))
        {
            Log("another Packetly instance is already serving — opening its dashboard");
            OpenDashboard(port);
            return 0;
        }
    }
    catch { /* nothing serving - continue with a fresh pipeline */ }

    var (db, _, tracker, _, writer, etw, poll, usedMode) = StartPipeline(dbPath, mode);
    Log($"collector: {usedMode}");
    var aggregator = new Aggregator(db);
    var resolver = new HostResolver(db);
    var alerts = new AlertEngine(db);
    var geoip = new GeoIpJob(db);
    db.AppCreated += (id, path) =>
    {
        try { alerts.OnAppCreated(id, path); } catch { /* alerting must never break collection */ }
        try { TryExtractIcon(db, (int)id, path); } catch { }
    };
    Log($"process tracker: {tracker.Count} processes resolved ({tracker.LastDiagnostic})");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory, // serve wwwroot from the output dir regardless of CWD
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    });
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    var app = builder.Build();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    AppApi.MapApi(app, new AppServices(db, writer, tracker, usedMode, elevated, Path.GetFullPath(dbPath), alerts));

    using var cts = new CancellationTokenSource();
    var ct = cts.Token;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); app.Lifetime.StopApplication(); };
    app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

    var trackerLoop = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { tracker.Refresh(); } catch { }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    });

    var writerLoop = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            writer.FlushOnce();
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
        writer.FlushOnce();
    });

    var aggregatorLoop = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { aggregator.RunCycle(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } catch (Exception ex) { Log($"[aggregator] {ex.Message}"); }
            try { await Task.Delay(30000, ct); } catch (OperationCanceledException) { break; }
        }
    });

    var resolverLoop = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await resolver.RunCycleAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(30000, ct); } catch (OperationCanceledException) { break; }
        }
    });

    var alertsLoop = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { alerts.RunCycle(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } catch (Exception ex) { Log($"[alerts] {ex.Message}"); }
            try { await Task.Delay(60000, ct); } catch (OperationCanceledException) { break; }
        }
    });

    // watchdog: restart collection after sleep/resume, ETW stream death, or crash
    bool paused = false;
    var watchdog = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(15000, ct);
                if (paused) continue;
                bool etwAlive = etw != null && etw.Running;
                bool pollAlive = poll != null && poll.Running;
                if (!etwAlive && !pollAlive)
                {
                    Log("collector not running — restarting (watchdog)");
                    if (mode is "auto" or "etw")
                    {
                        etw = new EtwNetworkCollector();
                        if (etw.Start(writer.OnEvent))
                        {
                            Log("collector restarted (etw)");
                        }
                        else
                        {
                            Log($"ETW restart failed: {etw.Error} — falling back to poll");
                            etw.Dispose();
                            etw = null;
                            poll = new PollCollector();
                            poll.Start(writer.OnEvent);
                            Log("collector restarted (poll)");
                        }
                    }
                    else
                    {
                        poll = new PollCollector();
                        poll.Start(writer.OnEvent);
                        Log("collector restarted (poll)");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"[watchdog] {ex.Message}"); }
        }
    });

    var geoipLoop = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await geoip.RunCycleAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(120000, ct); } catch (OperationCanceledException) { break; }
        }
    });

    _ = Task.Run(async () =>
    {
        // backfill icons for apps registered before this feature existed
        try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        foreach (var (appId, path) in db.AppsWithoutIcon(300))
        {
            if (ct.IsCancellationRequested) return;
            try { TryExtractIcon(db, appId, path); } catch { }
        }
    });

    string TogglePause()
    {
        if (!paused)
        {
            StopPipeline(etw, poll);
            paused = true;
            return "Resume collection";
        }
        if (mode is "auto" or "etw")
        {
            etw = new EtwNetworkCollector();
            if (etw.Start(writer.OnEvent))
            {
                poll?.Dispose();
                poll = null;
                paused = false;
                return "Pause collection";
            }
            etw.Dispose();
            etw = null;
        }
        poll = new PollCollector();
        poll.Start(writer.OnEvent);
        paused = false;
        return "Pause collection";
    }

    using var tray = new TrayController(
        () => OpenDashboard(port),
        () => { cts.Cancel(); app.Lifetime.StopApplication(); },
        TogglePause);

    Log($"API listening on http://127.0.0.1:{port}/");
    CreateDashboardShortcuts(port);

    // open the dashboard window shortly after startup (GlassWire-like behavior)
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(2500, ct);
            Log("opening dashboard window");
            OpenDashboard(port);
        }
        catch (OperationCanceledException) { }
    });

    try
    {
        await app.RunAsync();
    }
    catch (OperationCanceledException) { }
    catch (IOException ex) when (ex.Message.Contains("already in use") || ex.Message.Contains("address already"))
    {
        Log("another Packetly instance is already serving — opening its dashboard");
        StopPipeline(etw, poll);
        OpenDashboard(port);
        return 0;
    }

    Log("stopping...");
    StopPipeline(etw, poll);
    await Task.WhenAll(Task.WhenAny(trackerLoop, Task.Delay(3000)), Task.WhenAny(writerLoop, Task.Delay(3000)),
                       Task.WhenAny(aggregatorLoop, Task.Delay(3000)), Task.WhenAny(resolverLoop, Task.Delay(3000)),
                       Task.WhenAny(alertsLoop, Task.Delay(3000)), Task.WhenAny(geoipLoop, Task.Delay(3000)),
                       Task.WhenAny(watchdog, Task.Delay(3000)));
    try { aggregator.RunCycle(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } catch { }
    Log("stopped.");
    return 0;
}

// ---- logging ----

static string LogPath()
{
    var dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packetly");
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, "service.log");
}

static void Log(string msg)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
    try
    {
        var path = LogPath();
        lock (LogGateHolder.Gate)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Move(path, path + ".old", true);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
    catch { }
    Console.WriteLine(line);
}


static void TryExtractIcon(Packetly.Storage.Db db, int appId, string path)
{
    try
    {
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon == null) return;
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        if (ms.Length > 0 && ms.Length < 200_000) db.SetAppIcon(appId, ms.ToArray());
    }
    catch { /* protected/unreadable exes simply get no icon */ }
}

static class LogGateHolder
{
    public static readonly object Gate = new();
}

