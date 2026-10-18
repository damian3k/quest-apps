using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace QuestApps;

// Several headsets at once: pick headsets, pick an app, then launch / lock / unlock / stop
// it on all of them in parallel. Kiosks are shared with the main window (KioskRegistry).
public partial class FleetWindow : Window
{
    private enum Outcome { Ok, Skipped, Failed }

    private readonly AdbService _adb;
    private readonly ObservableCollection<FleetHeadset> _headsets = new();
    private readonly Dictionary<string, FleetApp> _appByPackage = new(StringComparer.Ordinal);
    private readonly HashSet<string> _detailsRequested = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshing;
    private bool _polling;
    private bool _busy;
    private int _pollCount;

    public FleetWindow() : this(new AdbService()) { } // designer

    public FleetWindow(AdbService adb)
    {
        InitializeComponent();
        _adb = adb.ForSerial(null);
        HeadsetList.ItemsSource = _headsets;

        RefreshBtn.Click += async (_, _) => await RefreshAsync();
        ScanBtn.Click += async (_, _) => await ScanAsync();
        SelectAllBtn.Click += (_, _) => SelectAll(true);
        SelectNoneBtn.Click += (_, _) => SelectAll(false);
        FilterBox.TextChanged += (_, _) => ShowApps();
        AppList.SelectionChanged += (_, _) => UpdateActions();

        LaunchBtn.Click += async (_, _) => await LaunchAsync(lockIt: false);
        LaunchLockBtn.Click += async (_, _) => await LaunchAsync(lockIt: true);
        UnlockBtn.Click += async (_, _) => await UnlockAsync();
        StopBtn.Click += async (_, _) => await StopAsync();
        AwakeOnBtn.Click += async (_, _) => await KeepAwakeAsync(true);
        AwakeOffBtn.Click += async (_, _) => await KeepAwakeAsync(false);

        KioskRegistry.Changed += OnKioskChanged;
        KioskRegistry.Status += OnKioskStatus;
        _poll.Tick += async (_, _) => await PollAsync();
        Opened += async (_, _) =>
        {
            await RefreshAsync();
            _poll.Start();
        };
        Closed += (_, _) =>
        {
            _poll.Stop();
            KioskRegistry.Changed -= OnKioskChanged;
            KioskRegistry.Status -= OnKioskStatus;
        };
        UpdateActions();
    }

    // ---- headsets ----

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            SetStatus("Looking for headsets…", FleetBrushes.Info);
            var list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probeRemembered: true));

            // remembered headsets that answer on Wi-Fi: connect them
            var nearby = list.Where(h => !h.IsConnected && h.WifiReachable && h.Ip != null).ToList();
            if (nearby.Count > 0)
            {
                SetStatus($"Connecting {nearby.Count} nearby headset(s) over Wi-Fi…", FleetBrushes.Info);
                await Task.Run(() => Parallel.ForEach(nearby, h => _adb.Connect(h.Ip!)));
                await Task.Delay(300);
                list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probeRemembered: true));
            }

            MergeHeadsets(list);
            await LoadPackagesAsync(_headsets.Where(h => h.IsOnline).ToList());
            RebuildApps();
            await UpdateLiveStateAsync();
            SetStatus(Summary(), FleetBrushes.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message, FleetBrushes.Err);
        }
        finally
        {
            _refreshing = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private string Summary()
    {
        int online = _headsets.Count(h => h.IsOnline);
        int locked = _headsets.Count(h => KioskRegistry.Get(h.Id) != null);
        var s = $"{online} of {_headsets.Count} headsets connected";
        if (locked > 0)
            s += $" • {locked} locked (kiosk)";
        if (online < _headsets.Count)
            s += ". Offline ones: plug in once and press Enable Wi-Fi in the main window (again after a headset " +
                 "restart), or use Find on Wi-Fi.";
        return s;
    }

    private async Task ScanAsync()
    {
        ScanBtn.IsEnabled = false;
        try
        {
            SetStatus("Searching the local network for headsets with Wi-Fi ADB…", FleetBrushes.Info);
            var ips = await Task.Run(() => DeviceDiscovery.ScanNetwork((d, t) =>
                Dispatcher.UIThread.Post(() => SetStatus($"Searching the local network… {d}/{t}", FleetBrushes.Info))));
            await Task.Run(() => Parallel.ForEach(ips, ip => _adb.Connect(ip)));
            SetStatus(ips.Count == 0
                ? "No headsets with Wi-Fi ADB found on the network."
                : $"Found {ips.Count}: {string.Join(", ", ips)}", ips.Count == 0 ? FleetBrushes.Warn : FleetBrushes.Ok);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Search error: " + ex.Message, FleetBrushes.Err);
        }
        finally
        {
            ScanBtn.IsEnabled = true;
        }
    }

    private void MergeHeadsets(List<Headset> list)
    {
        foreach (var h in list)
        {
            var fh = _headsets.FirstOrDefault(x => x.Id == h.Id);
            if (fh == null)
            {
                fh = new FleetHeadset(h.Id, h.Model) { Name = h.Name, IsSelected = h.IsConnected };
                fh.PropertyChanged += OnHeadsetChanged;
                _headsets.Add(fh);
            }
            bool cameOnline = !fh.IsOnline && h.IsConnected;
            fh.Model = h.Model;
            fh.Ip = h.Ip ?? fh.Ip;
            fh.OfflineText = h.Problem != null
                ? $"{h.Problem} — accept USB debugging in the headset"
                : h.WifiReachable ? $"nearby ({h.Ip})" : "offline" + (h.Ip != null ? $" (last seen at {h.Ip})" : "");
            fh.Serial = h.Serial;
            if (cameOnline && _headsets.Count(x => x.IsOnline) == 1)
                fh.IsSelected = true;
            if (!fh.IsOnline)
            {
                fh.BatteryText = "";
                fh.NowText = "";
            }
        }
        foreach (var gone in _headsets.Where(x => list.All(h => h.Id != x.Id)).ToList())
            _headsets.Remove(gone);
        HeadsetsSummary.Text = $"Headsets ({_headsets.Count(h => h.IsOnline)} connected)";
    }

    private void OnHeadsetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FleetHeadset.IsSelected) or nameof(FleetHeadset.IsOnline))
        {
            RecountApps();
            UpdateActions();
        }
    }

    private void SelectAll(bool selected)
    {
        foreach (var h in _headsets)
            h.IsSelected = selected && h.IsOnline;
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FleetHeadset h })
            HeadsetMemory.SetName(h.Id, h.Model, h.Name);
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    // ---- apps ----

    private async Task LoadPackagesAsync(List<FleetHeadset> headsets)
    {
        var results = await Task.Run(() => headsets.AsParallel().Select(h =>
        {
            try { return (h, Packages: _adb.ForSerial(h.Serial).ListUserPackages()); }
            catch { return (h, Packages: (Dictionary<string, string>?)null); }
        }).ToList());
        foreach (var (h, packages) in results)
            if (packages != null)
                h.Packages = packages;
    }

    private void RebuildApps()
    {
        var online = _headsets.Where(h => h.IsOnline).ToList();
        var packages = online.SelectMany(h => h.Packages.Keys).Distinct(StringComparer.Ordinal).ToList();
        var missing = new List<string>();
        foreach (var package in packages)
        {
            if (_appByPackage.ContainsKey(package))
                continue;
            var app = new FleetApp(package);
            var cached = AppCache.Load(package);
            if (cached != null)
                ApplyInfo(app, cached);
            else
                missing.Add(package);
            _appByPackage[package] = app;
        }
        RecountApps();
        _ = LoadMissingDetailsAsync(missing);
    }

    // Names/icons not in the local cache yet: read them from the APK on a headset that has it.
    private async Task LoadMissingDetailsAsync(List<string> packages)
    {
        var todo = packages.Where(p => _detailsRequested.Add(p)).ToList();
        if (todo.Count == 0)
            return;
        var sources = todo.Select(p =>
        {
            var h = _headsets.FirstOrDefault(x => x.IsOnline && x.Packages.ContainsKey(p));
            return (Package: p, Serial: h?.Serial, Apk: h?.Packages[p]);
        }).Where(s => s.Serial != null).ToList();

        await Task.Run(() => Parallel.ForEach(sources, new ParallelOptions { MaxDegreeOfParallelism = 4 }, s =>
        {
            try
            {
                var info = ApkLabelReader.ReadInfo(new DeviceApkSource(_adb.ForSerial(s.Serial), s.Apk!));
                if (info == null)
                    return;
                AppCache.Save(s.Package, info);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_appByPackage.TryGetValue(s.Package, out var app))
                    {
                        ApplyInfo(app, info);
                        ShowApps();
                    }
                });
            }
            catch { /* unreadable APK — keep the package name */ }
        }));
    }

    private static void ApplyInfo(FleetApp app, ApkInfo info)
    {
        if (!string.IsNullOrEmpty(info.Label))
            app.Title = info.Label;
        if (info.IconPng is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(info.IconPng);
                app.Icon = new Bitmap(ms);
            }
            catch { /* undecodable icon */ }
        }
    }

    private List<FleetHeadset> SelectedOnline() => _headsets.Where(h => h.IsSelected && h.IsOnline).ToList();

    private void RecountApps()
    {
        var selected = SelectedOnline();
        foreach (var app in _appByPackage.Values)
            app.SetCount(selected.Count(h => h.Packages.ContainsKey(app.Package)), selected.Count);
        ShowApps();
    }

    // Apps on at least one selected headset (all apps when nothing is selected), best coverage first.
    private void ShowApps()
    {
        var filter = FilterBox.Text?.Trim() ?? "";
        bool anySelected = SelectedOnline().Count > 0;
        var online = _headsets.Where(h => h.IsOnline).ToList();
        var apps = _appByPackage.Values
            .Where(a => online.Any(h => h.Packages.ContainsKey(a.Package)))
            .Where(a => !anySelected || a.Count > 0)
            .Where(a => filter.Length == 0 ||
                        a.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        a.Package.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chosen = (AppList.SelectedItem as FleetApp)?.Package;
        AppList.ItemsSource = apps;
        AppList.SelectedItem = apps.FirstOrDefault(a => a.Package == chosen);
        AppsSummary.Text = anySelected ? $"Apps on the selected headsets ({apps.Count})" : $"Apps ({apps.Count})";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var app = AppList.SelectedItem as FleetApp;
        var selected = SelectedOnline();
        bool any = selected.Count > 0 && !_busy;
        LaunchBtn.IsEnabled = LaunchLockBtn.IsEnabled = StopBtn.IsEnabled = any && app != null;
        UnlockBtn.IsEnabled = AwakeOnBtn.IsEnabled = AwakeOffBtn.IsEnabled = any;

        if (app == null)
        {
            ChosenText.Text = selected.Count == 0
                ? "Select headsets on the left, then pick an app."
                : $"{selected.Count} headset(s) selected — pick an app to start it on all of them at once.";
            return;
        }
        var without = selected.Where(h => !h.Packages.ContainsKey(app.Package)).Select(h => h.Label).ToList();
        ChosenText.Text = $"{app.DisplayName}: installed on {selected.Count - without.Count} of {selected.Count} " +
                          "selected headset(s)." +
                          (without.Count > 0 ? $" Not on: {string.Join(", ", without)} — these are skipped." : "");
    }

    // ---- live state (every 3 s) ----

    private async Task PollAsync()
    {
        if (_polling || _refreshing)
            return;
        _polling = true;
        try
        {
            bool probe = _pollCount++ % 5 == 0;
            var list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probe));
            var wasOnline = _headsets.Where(h => h.IsOnline).Select(h => h.Id).ToHashSet();
            MergeHeadsets(list);
            var newOnline = _headsets.Where(h => h.IsOnline && !wasOnline.Contains(h.Id)).ToList();
            if (newOnline.Count > 0)
            {
                await LoadPackagesAsync(newOnline);
                RebuildApps();
            }
            await UpdateLiveStateAsync();
        }
        catch { /* next tick */ }
        finally
        {
            _polling = false;
        }
    }

    private async Task UpdateLiveStateAsync()
    {
        var online = _headsets.Where(h => h.IsOnline).ToList();
        var snaps = await Task.Run(() => online.AsParallel().Select(h =>
        {
            try { return (h, Snap: ((int?, bool, string?, bool)?)_adb.ForSerial(h.Serial).GetStatusSnapshot()); }
            catch { return (h, Snap: ((int?, bool, string?, bool)?)null); }
        }).ToList());

        foreach (var (h, snap) in snaps)
        {
            if (snap is not { } s)
                continue;
            var (battery, charging, top, pinned) = s;
            h.BatteryText = battery == null ? "" : (charging ? "⚡ " : "🔋 ") + battery + "%";
            h.NowText = "Now: " + (top == null ? "—" : AppName(top));
            var guard = KioskRegistry.Get(h.Id);
            h.KioskText = guard != null
                ? $"🔒 {AppName(guard.Package)}" + (guard.Returns + guard.Restarts > 0
                    ? $" · brought back {guard.Returns}×, restarted {guard.Restarts}×" : "")
                : pinned ? "📌 pinned (not guarded)" : "";
        }
    }

    private string AppName(string package) => package switch
    {
        "com.oculus.vrshell" => "Meta home / menu",
        _ => _appByPackage.TryGetValue(package, out var a) ? a.DisplayName
            : AppCache.Load(package)?.Label ?? package,
    };

    private void OnKioskChanged() => Dispatcher.UIThread.Post(async () => await UpdateLiveStateAsync());

    private void OnKioskStatus(KioskGuard guard, string message, bool warning) => Dispatcher.UIThread.Post(() =>
        _headsets.FirstOrDefault(h => h.Id == guard.HeadsetId)?
            .SetStatus($"Kiosk: {AppName(guard.Package)} {message}", warning ? FleetBrushes.Warn : FleetBrushes.Ok));

    // ---- actions on all selected headsets, in parallel ----

    private async Task RunOnSelectedAsync(string what, Func<FleetHeadset, AdbService, (Outcome, string)> action)
    {
        var targets = SelectedOnline();
        if (targets.Count == 0)
        {
            SetStatus("Select at least one connected headset.", FleetBrushes.Warn);
            return;
        }
        _busy = true;
        UpdateActions();
        foreach (var t in targets)
            t.SetStatus(what + "…", FleetBrushes.Info);
        SetStatus($"{what} on {targets.Count} headset(s)…", FleetBrushes.Info);

        var results = await Task.WhenAll(targets.Select(t => Task.Run(() =>
        {
            try
            {
                // follow the headset if it moved between USB and Wi-Fi
                var serial = DeviceDiscovery.ResolveSerial(_adb, t.Id, t.Ip);
                if (serial == null)
                    return (t, Outcome.Failed, "not reachable");
                var (outcome, message) = action(t, _adb.ForSerial(serial));
                return (t, outcome, message);
            }
            catch (Exception ex)
            {
                return (t, Outcome.Failed, "error: " + ex.Message);
            }
        })));

        foreach (var (t, outcome, message) in results)
            t.SetStatus(message, outcome switch
            {
                Outcome.Ok => FleetBrushes.Ok,
                Outcome.Skipped => FleetBrushes.Warn,
                _ => FleetBrushes.Err,
            });
        int ok = results.Count(r => r.Item2 == Outcome.Ok);
        int skipped = results.Count(r => r.Item2 == Outcome.Skipped);
        int failed = results.Count(r => r.Item2 == Outcome.Failed);
        SetStatus($"{what}: done on {ok} headset(s)" +
                  (skipped > 0 ? $", {skipped} skipped" : "") + (failed > 0 ? $", {failed} failed" : "") +
                  " — details next to each headset.",
            failed > 0 ? FleetBrushes.Err : skipped > 0 ? FleetBrushes.Warn : FleetBrushes.Ok);

        _busy = false;
        UpdateActions();
        await UpdateLiveStateAsync();
    }

    private async Task LaunchAsync(bool lockIt)
    {
        if (AppList.SelectedItem is not FleetApp app)
            return;
        var package = app.Package;
        var name = app.DisplayName;
        await RunOnSelectedAsync(lockIt ? $"Launch & lock {name}" : $"Launch {name}", (h, adb) =>
        {
            if (!h.Packages.ContainsKey(package))
                return (Outcome.Skipped, $"{name} is not installed — skipped");

            var guard = KioskRegistry.Get(h.Id);
            if (!lockIt && guard != null && guard.Package != package)
                return (Outcome.Skipped, $"locked to {AppName(guard.Package)} — unlock first");
            // a pin blocks starting another app: lift the old kiosk first
            if (lockIt && (guard != null || adb.GetKioskSnapshot(package).Pinned))
                KioskRegistry.Unlock(_adb, h.Id, adb.Serial);

            var error = adb.Launch(package);
            if (error != null)
                return (Outcome.Failed, $"could not start {name}: {error}");
            if (!lockIt)
                return (Outcome.Ok, $"{name} started");

            // VR apps need a moment for their process and window before they can be pinned
            for (int i = 0; i < 20; i++)
            {
                var snap = adb.GetKioskSnapshot(package);
                if (snap.Running && snap.Foreground == package)
                    break;
                System.Threading.Thread.Sleep(600);
            }
            var lockError = KioskRegistry.Lock(_adb, h.Id, adb.Serial!, h.Ip, package);
            return lockError == null
                ? (Outcome.Ok, $"{name} running and locked (kiosk)")
                : (Outcome.Failed, $"{name} started, but locking failed: {lockError}");
        });
    }

    private Task UnlockAsync() => RunOnSelectedAsync("Unlock", (h, adb) =>
    {
        var guard = KioskRegistry.Get(h.Id);
        KioskRegistry.Unlock(_adb, h.Id, adb.Serial); // also lifts a pin that is not guarded
        return (Outcome.Ok, guard != null ? $"{AppName(guard.Package)} unlocked" : "unlocked");
    });

    private async Task StopAsync()
    {
        if (AppList.SelectedItem is not FleetApp app)
            return;
        var package = app.Package;
        var name = app.DisplayName;
        await RunOnSelectedAsync($"Stop {name}", (h, adb) =>
        {
            if (!h.Packages.ContainsKey(package))
                return (Outcome.Skipped, $"{name} is not installed — skipped");
            if (KioskRegistry.Get(h.Id)?.Package == package)
                KioskRegistry.Unlock(_adb, h.Id, adb.Serial);
            adb.ForceStop(package);
            return (Outcome.Ok, $"{name} stopped");
        });
    }

    private Task KeepAwakeAsync(bool on) => RunOnSelectedAsync(on ? "Keep awake on" : "Keep awake off", (_, adb) =>
    {
        adb.SetKeepAwake(on);
        return (Outcome.Ok, on ? "keep awake on — stays on when taken off" : "keep awake off");
    });

    private void SetStatus(string text, IBrush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }
}
