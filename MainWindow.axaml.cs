using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace QuestApps;

public partial class MainWindow : Window
{
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#FF8B8CA0"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#FF57D98A"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFE8B44C"));
    private static readonly IBrush ErrBrush = new SolidColorBrush(Color.Parse("#FFFF6B6B"));

    private readonly AdbService _adb = new();
    private List<QuestApp> _apps = new();
    private List<LargeFile> _files = new();
    private (long TotalKb, long UsedKb) _storage;
    private readonly ObservableCollection<InstallItem> _queue = new();
    private CancellationTokenSource? _installCts;

    // headsets
    private List<Headset> _headsets = new();
    private string? _selectedId = HeadsetMemory.Selected;
    private string? _loadedFor;       // headset whose apps are on screen
    private string _deviceListSig = "";
    private bool _updatingDeviceBox;
    private bool _refreshing;
    private readonly HashSet<string> _wifiOptOut = new(); // Wi-Fi disconnected on purpose this session

    // running apps + kiosk
    private HashSet<string> _running = new();
    // kiosk of the headset on screen (kiosks themselves live in KioskRegistry)
    private string? _lockedPackage => KioskRegistry.Get(_loadedFor)?.Package;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _polling;
    private int _pollCount;
    private bool _closeConfirmed;

    public MainWindow() : this(Array.Empty<string>()) { }

    // args: APK files/folders dropped onto the exe or opened with it go straight to the install queue
    public MainWindow(string[] args)
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version != null)
            Title = $"Quest Apps {version.Major}.{version.Minor} — what's on my headset";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://QuestApps/icon256.png")));
        RefreshBtn.Click += async (_, _) => await RefreshAsync();
        WifiBtn.Click += async (_, _) => await WifiButtonAsync();
        FleetBtn.Click += (_, _) => OpenFleet();
        ScanBtn.Click += async (_, _) => await ScanNetworkAsync();
        AwakeCheck.Click += async (_, _) => await ToggleKeepAwakeAsync();
        MtpBtn.Click += async (_, _) => await ToggleMtpAsync();
        DeviceBox.SelectionChanged += async (_, _) => await OnDeviceSelectedAsync();
        SortBox.SelectionChanged += (_, _) => Populate();
        StoreSearch.TextChanged += (_, _) => Populate();
        SideSearch.TextChanged += (_, _) => Populate();
        Loaded += (_, _) => _ = InitAsync();
        Closing += OnWindowClosing;
        _poll.Tick += async (_, _) => await PollAsync();
        KioskRegistry.Changed += () => Dispatcher.UIThread.Post(ApplyRunState);
        KioskRegistry.Status += (guard, message, warning) => Dispatcher.UIThread.Post(() =>
        {
            if (guard.HeadsetId == _loadedFor)
                SetStatus($"Kiosk: {AppName(guard.Package)} {message}", warning ? WarnBrush : OkBrush);
        });

        InstallList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => UpdateQueueUi();
        PickApkBtn.Click += async (_, _) => await PickApksAsync();
        PickFolderBtn.Click += async (_, _) => await PickFolderAsync();
        InstallAllBtn.Click += async (_, _) => await InstallQueueAsync();
        CancelInstallBtn.Click += (_, _) => _installCts?.Cancel();
        ClearDoneBtn.Click += (_, _) =>
        {
            foreach (var item in _queue.Where(i => i.State == InstallState.Done).ToList())
                _queue.Remove(item);
        };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragover", false));
        AddHandler(DragDrop.DropEvent, OnDrop);

        var startupPaths = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
        if (startupPaths.Count > 0)
            Loaded += async (_, _) => await AddToQueueAsync(startupPaths);
    }

    private async Task InitAsync()
    {
        foreach (var candidate in AdbService.FindAdbCandidates())
        {
            var ok = await Task.Run(() => _adb.TrySetAdb(candidate));
            if (!ok)
                continue;
            RefreshBtn.IsEnabled = true;
            FleetBtn.IsEnabled = true;
            UpdateQueueUi();
            SetStatus($"adb ready ({Path.GetFileName(_adb.AdbPath)}) — looking for headsets…", InfoBrush);
            await RefreshAsync();
            _poll.Start();
            return;
        }

        SetStatus(OperatingSystem.IsWindows()
            ? "adb.exe not found — it should live in the app folder under adb\\. " +
              "Reinstall the app or add Android platform-tools to PATH."
            : OperatingSystem.IsMacOS()
                ? "adb not found — install it with “brew install --cask android-platform-tools”, " +
                  "or put Google's platform-tools into the adb folder next to the app."
                : "adb not found — install it (e.g. “sudo apt install adb”), " +
                  "or put Google's platform-tools into the adb folder next to the app.", ErrBrush);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            SetStatus("Looking for headsets…", InfoBrush);
            var h = await EnsureConnectedAsync();
            if (h == null)
            {
                ClearDeviceData();
                return;
            }

            _loadedFor = h.Id;
            SetStatus($"Connected: {Describe(h)} — scanning apps and storage…", OkBrush);
            var apps = await Task.Run(_adb.GetApps);
            var storage = await Task.Run(_adb.GetStorage);
            _apps = apps;
            _storage = storage;
            Populate();

            var lockInfo = await Task.Run(_adb.GetLockInfo);
            // a lock already on the headset (e.g. from an earlier session) — keep guarding it
            if (lockInfo.Package != null && h.Serial != null)
                KioskRegistry.Adopt(_adb, h.Id, h.Serial, h.Ip, lockInfo.Package);
            await RefreshRunningAsync();
            AwakeCheck.IsChecked = await Task.Run(_adb.GetKeepAwake);
            MtpBtn.IsChecked = await Task.Run(_adb.GetMtp);

            var store = apps.Count(a => a.IsStore);
            SetStatus($"{Describe(h)} — {apps.Count} apps: {store} from the store, " +
                      $"{apps.Count - store} sideloaded. Loading names/icons…", OkBrush);

            var filesTask = Task.Run(_adb.GetLargeFiles);
            await Task.Run(() => LoadDetails(apps));
            _files = await filesTask;
            PopulateFiles();
            await RefreshStorageOnly();

            UpdateExistingLabels();
            var kiosk = _lockedPackage != null ? $" Kiosk: {AppName(_lockedPackage)} is locked." : "";
            SetStatus($"{Describe(h)} — {apps.Count} apps: {store} from the store, " +
                      $"{apps.Count - store} sideloaded.{kiosk}", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message, ErrBrush);
        }
        finally
        {
            _refreshing = false;
            RefreshBtn.IsEnabled = _adb.IsReady && _installCts == null;
            UpdateDeviceButtons();
        }
    }

    private void ClearDeviceData()
    {
        _loadedFor = null;
        _apps = new();
        _files = new();
        _storage = (0, 0);
        _running = new();
        Populate();
        PopulateFiles();
    }

    // ---- multi-headset window ----

    private FleetWindow? _fleet;

    private void OpenFleet()
    {
        if (_fleet != null)
        {
            _fleet.Activate();
            return;
        }
        _fleet = new FleetWindow(_adb) { Icon = Icon };
        _fleet.Closed += (_, _) => _fleet = null;
        _fleet.Show();
    }

    // ---- headsets ----

    private Headset? SelectedHeadset => _headsets.FirstOrDefault(h => h.Id == _selectedId);

    // The selected headset if it is (or may become) usable; otherwise the first connected one.
    private Headset? ChooseHeadset(List<Headset> list)
    {
        var selected = list.FirstOrDefault(h => h.Id == _selectedId);
        if (selected != null && (selected.IsConnected || selected.Problem != null ||
                                 (selected.Ip != null && !_wifiOptOut.Contains(selected.Id))))
            return selected;
        var fallback = list.FirstOrDefault(h => h.IsConnected || h.Problem != null) ?? selected ?? list.FirstOrDefault();
        if (fallback != null && (fallback.IsConnected || fallback.Problem != null))
        {
            _selectedId = fallback.Id;
            HeadsetMemory.Selected = fallback.Id;
        }
        return fallback;
    }

    // Points _adb.Serial at the selected headset before an operation; when the cable is gone
    // it reconnects over Wi-Fi. Returns null (with a status message) when not reachable.
    private async Task<Headset?> EnsureConnectedAsync()
    {
        var list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probeRemembered: false));
        var h = ChooseHeadset(list);
        if (h is { IsConnected: false, Problem: null, Ip: { } ip } && !_wifiOptOut.Contains(h.Id))
        {
            SetStatus($"Connecting to {h.Model} over Wi-Fi ({ip})…", InfoBrush);
            if (await Task.Run(() => AdbService.CanReach(ip) && _adb.Connect(ip)))
            {
                await Task.Delay(300);
                list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probeRemembered: false));
                h = list.FirstOrDefault(x => x.Id == h.Id) ?? h;
            }
        }

        ShowHeadsets(list);
        _adb.Serial = h?.Serial;
        UpdateDeviceButtons();

        if (h == null)
        {
            SetStatus("No headset. Connect the Quest over USB (developer mode on, headset awake) " +
                      "and press Refresh.", WarnBrush);
            return null;
        }
        if (h.Problem != null)
        {
            SetStatus($"The headset is “{h.Problem}” — put it on and accept the “Allow USB debugging” prompt.",
                WarnBrush);
            return null;
        }
        if (!h.IsConnected)
        {
            SetStatus($"{h.Model} is not reachable. Plug in the cable — or, if Wi-Fi ADB is on, wake the " +
                      "headset and make sure it is on the same network (try “Find on Wi-Fi”).", WarnBrush);
            return null;
        }
        return h;
    }

    private void ShowHeadsets(List<Headset> list)
    {
        _headsets = list;
        var sig = string.Join("|", list.Select(h => h.Id + "=" + h.DisplayText)) + "#" + _selectedId;
        if (sig == _deviceListSig || DeviceBox.IsDropDownOpen)
            return;
        _deviceListSig = sig;
        _updatingDeviceBox = true;
        DeviceBox.ItemsSource = list;
        DeviceBox.SelectedItem = list.FirstOrDefault(h => h.Id == _selectedId);
        _updatingDeviceBox = false;
    }

    private async Task OnDeviceSelectedAsync()
    {
        if (_updatingDeviceBox || DeviceBox.SelectedItem is not Headset h || h.Id == _selectedId && h.IsConnected)
            return;
        _selectedId = h.Id;
        HeadsetMemory.Selected = h.Id;
        _wifiOptOut.Remove(h.Id);
        await RefreshAsync();
    }

    private static string Describe(Headset h) =>
        $"{h.Model.Replace('_', ' ')} ({(h.UsbSerial != null ? "USB" : "Wi-Fi " + h.Ip)})";

    private void UpdateDeviceButtons()
    {
        var h = SelectedHeadset;
        bool idle = _adb.IsReady && _installCts == null;
        DeviceBox.IsEnabled = idle;
        ScanBtn.IsEnabled = idle;
        if (h?.WifiSerial != null)
        {
            WifiBtn.Content = "Disconnect Wi-Fi";
            WifiBtn.IsEnabled = idle;
        }
        else if (h?.UsbSerial != null)
        {
            WifiBtn.Content = "Enable Wi-Fi";
            WifiBtn.IsEnabled = idle;
        }
        else
        {
            WifiBtn.Content = "Connect Wi-Fi";
            WifiBtn.IsEnabled = idle && h?.Ip != null;
        }
        AwakeCheck.IsEnabled = h?.IsConnected == true;
        MtpBtn.IsEnabled = h?.IsConnected == true;
    }

    // ---- Wi-Fi ADB ----

    private async Task WifiButtonAsync()
    {
        var h = SelectedHeadset;
        if (h == null)
            return;
        WifiBtn.IsEnabled = false;
        try
        {
            if (h.WifiSerial != null)
            {
                _wifiOptOut.Add(h.Id);
                var serial = h.WifiSerial;
                await Task.Run(() => _adb.Disconnect(serial));
                SetStatus(h.UsbSerial != null
                    ? "Wi-Fi ADB disconnected — still connected by USB."
                    : "Wi-Fi ADB disconnected. Pick the headset in the list to connect again.", InfoBrush);
            }
            else if (h.UsbSerial != null)
            {
                _wifiOptOut.Remove(h.Id);
                var usb = h.UsbSerial;
                var (error, ip) = await Task.Run(() => EnableWifi(usb));
                SetStatus(error ?? $"Wi-Fi ADB is on ({ip}). You can unplug the cable — the app switches to Wi-Fi " +
                                   "by itself. Tip: “Keep awake” stops the headset from sleeping (and dropping Wi-Fi) " +
                                   "when it is not worn.", error == null ? OkBrush : WarnBrush);
            }
            else
            {
                _wifiOptOut.Remove(h.Id);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Wi-Fi error: " + ex.Message, ErrBrush);
        }

        var status = StatusText.Text;
        var brush = StatusText.Foreground ?? InfoBrush;
        var now = await EnsureConnectedAsync();
        if (now != null && now.Id != _loadedFor)
            await RefreshAsync();
        else if (now != null)
            SetStatus(status ?? "", brush);
    }

    // Runs off the UI thread.
    private (string? Error, string? Ip) EnableWifi(string usbSerial)
    {
        void Status(string text) => Dispatcher.UIThread.Post(() => SetStatus(text, InfoBrush));

        var ip = _adb.GetWifiIp(usbSerial);
        if (ip == null)
            return ("The headset is not connected to Wi-Fi — join the same network as this PC and try again.", null);
        if (!_adb.IsTcpipEnabled(usbSerial))
        {
            Status($"Turning on Wi-Fi ADB ({ip})…");
            _adb.EnableTcpip(usbSerial); // adbd restarts — USB drops for a moment
        }

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            Status($"Connecting to {ip}:{AdbService.WifiPort}… ({attempt}/10)");
            if (AdbService.CanReach(ip) && _adb.Connect(ip))
                return (null, ip);
            Thread.Sleep(1000);
        }
        return ($"Wi-Fi ADB is on, but this PC cannot reach {ip}:{AdbService.WifiPort}. Check that the PC and " +
                "the headset are on the same network and that no VPN, guest network or firewall blocks it.", ip);
    }

    private async Task ScanNetworkAsync()
    {
        ScanBtn.IsEnabled = false;
        try
        {
            SetStatus("Searching the local network for headsets with Wi-Fi ADB…", InfoBrush);
            var ips = await Task.Run(() => DeviceDiscovery.ScanNetwork((d, t) =>
                Dispatcher.UIThread.Post(() => SetStatus($"Searching the local network… {d}/{t}", InfoBrush))));
            foreach (var ip in ips)
                await Task.Run(() => _adb.Connect(ip));
            await Task.Delay(300);
            var list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probeRemembered: true));
            _deviceListSig = "";
            ShowHeadsets(list);

            if (ips.Count == 0)
            {
                SetStatus("No headsets with Wi-Fi ADB found. Wi-Fi ADB has to be turned on over USB once after " +
                          "every headset restart (Enable Wi-Fi), and the headset must be awake.", WarnBrush);
                return;
            }
            var names = list.Where(h => h.WifiSerial != null).Select(h => $"{h.Model.Replace('_', ' ')} ({h.Ip})");
            SetStatus($"Found: {string.Join(", ", names)} — pick a headset in the list.", OkBrush);
            if (_loadedFor == null)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Search error: " + ex.Message, ErrBrush);
        }
        finally
        {
            UpdateDeviceButtons();
        }
    }

    private async Task ToggleKeepAwakeAsync()
    {
        bool on = AwakeCheck.IsChecked == true;
        if (await EnsureConnectedAsync() == null)
        {
            AwakeCheck.IsChecked = !on;
            return;
        }
        try
        {
            await Task.Run(() => _adb.SetKeepAwake(on));
            SetStatus(on
                ? "Keep awake on — the headset stays on when taken off (until it restarts or you turn this off)."
                : "Keep awake off — the headset sleeps normally when taken off.", OkBrush);
        }
        catch (Exception ex)
        {
            AwakeCheck.IsChecked = !on;
            SetStatus("Keep awake error: " + ex.Message, ErrBrush);
        }
    }

    private async Task ToggleMtpAsync()
    {
        bool on = MtpBtn.IsChecked == true;
        var h = await EnsureConnectedAsync();
        if (h == null)
        {
            MtpBtn.IsChecked = !on;
            return;
        }
        MtpBtn.IsEnabled = false;
        try
        {
            await Task.Run(() => _adb.SetMtp(on));
            var now = await Task.Run(_adb.GetMtp);
            MtpBtn.IsChecked = now;
            if (now != on)
                SetStatus(on ? "The headset did not switch USB to MTP." : "The headset is still in MTP mode.", WarnBrush);
            else if (on)
                SetStatus(h.UsbSerial != null
                    ? "MTP on — the headset now shows up in your computer's file explorer (USB). " +
                      "Put the headset on and allow file access if it asks."
                    : "MTP on — connect the USB cable to browse the headset in your computer's file explorer.", OkBrush);
            else
                SetStatus("MTP off — USB is back to ADB only.", OkBrush);
        }
        catch (Exception ex)
        {
            MtpBtn.IsChecked = !on;
            SetStatus("MTP error: " + ex.Message, ErrBrush);
        }
        finally
        {
            MtpBtn.IsEnabled = SelectedHeadset?.IsConnected == true;
        }
    }

    // ---- background: device changes, running apps, kiosk watchdog ----

    private async Task PollAsync()
    {
        if (_polling || _refreshing || !_adb.IsReady)
            return;
        _polling = true;
        try
        {
            bool probe = _pollCount++ % 5 == 0;
            var list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probe));
            var h = ChooseHeadset(list);

            // cable unplugged / Wi-Fi dropped: reconnect over Wi-Fi if the headset answers
            if (h is { IsConnected: false, Problem: null, Ip: { } ip } && !_wifiOptOut.Contains(h.Id) &&
                await Task.Run(() => AdbService.CanReach(ip) && _adb.Connect(ip)))
            {
                list = await Task.Run(() => DeviceDiscovery.Discover(_adb, probeRemembered: false));
                h = list.FirstOrDefault(x => x.Id == h.Id) ?? h;
            }
            ShowHeadsets(list);

            var before = _adb.Serial;
            var serial = h?.Serial;
            if (serial != before)
            {
                _adb.Serial = serial;
                UpdateDeviceButtons();
                if (serial == null)
                    SetStatus($"{h?.Model ?? "Headset"} disconnected.", WarnBrush);
                else if (before != null)
                    SetStatus(AdbService.IsWifiSerial(serial)
                        ? $"Cable unplugged — continuing over Wi-Fi ({h!.Ip})."
                        : "Cable connected — using USB.", OkBrush);
            }
            else
            {
                UpdateDeviceButtons();
            }

            if (h == null || serial == null)
                return;
            if (h.Id != _loadedFor && _installCts == null)
            {
                _polling = false;
                await RefreshAsync(); // a headset showed up — load its apps
                return;
            }

            await RefreshRunningAsync();
        }
        catch { /* next tick */ }
        finally
        {
            _polling = false;
        }
    }

    private async Task RefreshRunningAsync()
    {
        try { _running = await Task.Run(_adb.GetRunningPackages); }
        catch { return; }
        ApplyRunState();
    }

    private void ApplyRunState()
    {
        foreach (var app in _apps)
        {
            app.IsRunning = _running.Contains(app.Package);
            app.IsLocked = app.Package == _lockedPackage;
        }
        foreach (var item in _queue)
        {
            item.IsRunning = item.Package != null && _running.Contains(item.Package);
            item.IsLocked = item.Package != null && item.Package == _lockedPackage;
        }
    }

    // Launch / Stop work on rows of the app lists and on installed rows of the install queue.
    private static (string Package, string Name)? RunTarget(object? dataContext) => dataContext switch
    {
        QuestApp app => (app.Package, app.DisplayName),
        InstallItem { Package: { } package } item => (package, item.DisplayName),
        _ => null,
    };

    private string AppName(string package) =>
        _apps.FirstOrDefault(a => a.Package == package)?.DisplayName ?? package;

    // ---- launch / stop / lock ----

    private async void OnLaunchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || RunTarget(button.DataContext) is not var (package, name))
            return;
        button.IsEnabled = false;
        try
        {
            if (await EnsureConnectedAsync() == null)
                return;
            SetStatus($"Starting {name}…", InfoBrush);
            var error = await Task.Run(() => _adb.Launch(package));
            if (error != null)
            {
                SetStatus($"Could not start {name}: {error}", ErrBrush);
                return;
            }
            // VR apps take a moment to create their process and window
            for (int i = 0; i < 6 && !_running.Contains(package); i++)
            {
                await Task.Delay(700);
                await RefreshRunningAsync();
            }
            SetStatus(_running.Contains(package)
                ? $"{name} is running — put the headset on. Use Lock for kiosk mode."
                : $"Started {name} — put the headset on.", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Launch error: " + ex.Message, ErrBrush);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || RunTarget(button.DataContext) is not var (package, name))
            return;
        button.IsEnabled = false;
        try
        {
            if (await EnsureConnectedAsync() == null)
                return;
            await Task.Run(() => _adb.ForceStop(package));
            await RefreshRunningAsync();
            bool still = _running.Contains(package);
            SetStatus(still ? $"{name} is still running." : $"Stopped {name}.", still ? WarnBrush : OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Stop error: " + ex.Message, ErrBrush);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnLockClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QuestApp app } button)
            return;
        button.IsEnabled = false;
        try
        {
            if (await EnsureConnectedAsync() is not { Serial: { } serial } h)
                return;
            var error = await Task.Run(() => KioskRegistry.Lock(_adb, h.Id, serial, h.Ip, app.Package));
            if (error != null)
            {
                SetStatus($"Could not lock {app.DisplayName}: {error}", ErrBrush);
                return;
            }
            ApplyRunState();
            SetStatus($"Kiosk on: {app.DisplayName} is locked. Other apps cannot be opened; if the Meta menu is " +
                      "opened the app comes back within about a second, and if it is quit it starts again. " +
                      "Works while this window is open and connected — use Wi-Fi + Keep awake. Unlock to end.", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Lock error: " + ex.Message, ErrBrush);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnUnlockClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QuestApp app } button)
            return;
        button.IsEnabled = false;
        try
        {
            var id = _loadedFor;
            if (id == null)
                return;
            var h = await EnsureConnectedAsync(); // when unreachable the watchdog still stops
            await Task.Run(() => KioskRegistry.Unlock(_adb, id, h?.Serial));
            ApplyRunState();
            SetStatus(h != null
                ? $"Kiosk off: {app.DisplayName} is unlocked."
                : $"Kiosk watchdog stopped for {app.DisplayName}; the headset could not be reached to unpin it.",
                h != null ? OkBrush : WarnBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Unlock error: " + ex.Message, ErrBrush);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        var kiosks = KioskRegistry.All();
        if (kiosks.Count == 0 || _closeConfirmed)
            return;
        e.Cancel = true;
        var heading = kiosks.Count == 1
            ? $"“{AppName(kiosks[0].Package)}” is locked (kiosk)"
            : $"{kiosks.Count} headsets are locked (kiosk)";
        var unlock = await ConfirmDialog.AskCustomAsync(this, heading,
            "Locked headsets stay locked after this window closes, but a kiosk app that gets closed or " +
            "left will no longer be brought back.\n\nUnlock before closing?",
            "Unlock & close", "Keep locked");
        if (unlock)
        {
            try { await Task.Run(KioskRegistry.UnlockAll); }
            catch { /* closing anyway */ }
        }
        _closeConfirmed = true;
        Close();
    }

    // Labels, versions and icons straight from each APK on the device.
    // Cached locally, so later scans are instant; device reads run in parallel.
    private void LoadDetails(List<QuestApp> apps)
    {
        int done = 0;
        int total = apps.Count;
        void Progress()
        {
            done++;
            if (done % 4 == 0 || done == total)
            {
                int d = done;
                Dispatcher.UIThread.Post(() =>
                    SetStatus($"Loading names/icons… {d}/{total}", InfoBrush));
            }
        }

        // instant part: apply the local cache
        var missing = new List<QuestApp>();
        foreach (var app in apps)
        {
            var cached = AppCache.Load(app.Package);
            if (cached != null)
            {
                var c = cached;
                Dispatcher.UIThread.Post(() => ApplyInfo(app, c));
                Progress();
            }
            else
            {
                missing.Add(app);
            }
        }

        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 4 }, app =>
        {
            try
            {
                var info = ApkLabelReader.ReadInfo(new DeviceApkSource(_adb, app.ApkPath));
                if (info != null)
                {
                    AppCache.Save(app.Package, info);
                    Dispatcher.UIThread.Post(() => ApplyInfo(app, info));
                }
            }
            catch { /* unreadable APK — keep package name */ }
            Progress();
        });
    }

    private void ApplyInfo(QuestApp app, ApkInfo info)
    {
        if (app.Title == null && !string.IsNullOrEmpty(info.Label))
            app.Title = info.Label;
        if (string.IsNullOrEmpty(app.Version) && !string.IsNullOrEmpty(info.VersionName))
            app.Version = info.VersionName;
        if (app.Icon == null && info.IconPng is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(info.IconPng);
                app.Icon = new Bitmap(ms);
            }
            catch { /* undecodable icon — skip */ }
        }
        Bar.InvalidateVisual();
    }

    private void PopulateFiles()
    {
        FilesList.ItemsSource = _files;
        FilesSummary.Text = _files.Count > 0
            ? $"{_files.Count} files ≥ 50 MB • {_fmtTotalFiles()} total"
            : "No files ≥ 50 MB found";
    }

    private string _fmtTotalFiles()
    {
        var kb = _files.Sum(f => Math.Max(f.SizeKb, 0));
        return Fmt(kb);
    }

    // After a file delete: re-read usage so the bar and summary stay honest.
    private async Task RefreshStorageOnly()
    {
        try
        {
            _storage = await Task.Run(_adb.GetStorage);
            Populate();
        }
        catch { /* keep previous numbers */ }
    }

    private async void OnDeleteFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: LargeFile file })
            return;

        var confirmed = await ConfirmDialog.AskFileAsync(this, file.Name, file.Path, file.SizeText);
        if (!confirmed)
            return;
        if (await EnsureConnectedAsync() == null)
            return;

        RefreshBtn.IsEnabled = false;
        SetStatus($"Deleting {file.Name}…", WarnBrush);
        try
        {
            var ok = await Task.Run(() => _adb.DeleteFile(file.Path));
            if (!ok)
            {
                SetStatus($"Could not delete {file.Path} (system refused).", ErrBrush);
                return;
            }
            _files.Remove(file);
            PopulateFiles();
            SetStatus($"Deleted {file.Name}. Updating storage…", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Delete error: " + ex.Message, ErrBrush);
            return;
        }
        finally
        {
            RefreshBtn.IsEnabled = _adb.IsReady;
        }
        await RefreshStorageOnly();
    }

    private void Populate()
    {
        var apps = SortBox.SelectedIndex == 1
            ? _apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
            : _apps.OrderByDescending(a => a.SizeKb).ToList();

        var store = apps.Where(a => a.IsStore).ToList();
        var side = apps.Where(a => !a.IsStore).ToList();
        var storeShown = Search(store, StoreSearch.Text);
        var sideShown = Search(side, SideSearch.Text);
        StoreList.ItemsSource = storeShown;
        SideList.ItemsSource = sideShown;
        StoreSummary.Text = ListSummary(store, storeShown);
        SideSummary.Text = ListSummary(side, sideShown);

        ApplyRunState();
        Bar.Apps = apps;
        Bar.TotalKb = _storage.TotalKb;
        Bar.UsedKb = _storage.UsedKb;
        Bar.Hovered = null;
        Bar.InvalidateVisual();

        if (_storage.TotalKb > 0)
        {
            var appsKb = apps.Sum(a => Math.Max(a.SizeKb, 0));
            var other = Math.Max(0, _storage.UsedKb - appsKb);
            var pct = 100.0 * _storage.UsedKb / _storage.TotalKb;
            StorageSummary.Text =
                $"Storage: {Fmt(_storage.UsedKb)} of {Fmt(_storage.TotalKb)} used ({pct:0.0}%) • apps: {Fmt(appsKb)} • app data (internal) + system: {Fmt(other)}";
        }
        else
        {
            StorageSummary.Text = "—";
        }
    }

    private void OnRowPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is Control { DataContext: QuestApp app })
        {
            Bar.Hovered = app;
            Bar.InvalidateVisual();
        }
    }

    private void OnRowPointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is Control { DataContext: QuestApp app } && ReferenceEquals(Bar.Hovered, app))
        {
            Bar.Hovered = null;
            Bar.InvalidateVisual();
        }
    }

    private async void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: QuestApp app })
            return;

        var confirmed = await ConfirmDialog.AskAsync(this, app.DisplayName, app.Package, app.SizeText);
        if (!confirmed)
            return;
        if (await EnsureConnectedAsync() == null)
            return;

        RefreshBtn.IsEnabled = false;
        SetStatus($"Uninstalling {app.Package}…", WarnBrush);
        try
        {
            var ok = await Task.Run(() => _adb.Uninstall(app.Package));
            if (!ok)
            {
                SetStatus($"Could not uninstall {app.Package} (system refused).", ErrBrush);
                return;
            }
            SetStatus($"Uninstalled {app.Package}. Refreshing…", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus("Uninstall error: " + ex.Message, ErrBrush);
            return;
        }
        finally
        {
            RefreshBtn.IsEnabled = _adb.IsReady;
        }
        await RefreshAsync();
    }

    // ---- Install apps ----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("dragover", hasFiles);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("dragover", false);
        var paths = e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToList();
        if (paths is { Count: > 0 })
            await AddToQueueAsync(paths);
    }

    private async Task PickApksAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose APK files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Android apps and OBB data") { Patterns = new[] { "*.apk", "*.obb" } },
            },
        });
        await AddToQueueAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder with APK (and OBB) files",
            AllowMultiple = true,
        });
        await AddToQueueAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    private async Task AddToQueueAsync(List<string> paths)
    {
        if (paths.Count == 0)
            return;
        Tabs.SelectedItem = InstallTab;
        QueueSummary.Text = "Reading APK files…";

        List<InstallItem> items;
        List<string> skipped;
        try
        {
            (items, skipped) = await Task.Run(() => InstallScanner.Collect(paths));
        }
        catch (Exception ex)
        {
            UpdateQueueUi();
            SetStatus("Could not read the dropped files: " + ex.Message, ErrBrush);
            return;
        }
        int added = 0;
        foreach (var item in items)
        {
            if (_queue.Any(q => q.State != InstallState.Done &&
                                string.Equals(q.ApkPath, item.ApkPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (item.IconPng is { Length: > 0 })
            {
                try
                {
                    using var ms = new MemoryStream(item.IconPng);
                    item.Icon = new Bitmap(ms);
                }
                catch { /* undecodable icon — skip */ }
            }
            _queue.Add(item);
            added++;
        }
        UpdateExistingLabels();
        UpdateQueueUi();

        if (skipped.Count > 0)
            SetStatus($"Added {added} to the install queue. Skipped: {string.Join(", ", skipped)}", WarnBrush);
        else if (added > 0)
            SetStatus($"Added {added} to the install queue — press Install.", InfoBrush);
    }

    private void OnRemoveQueuedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InstallItem { CanRemove: true } item })
            _queue.Remove(item);
    }

    // Installs the same APK (and OBB) once more — just this row, not the rest of the queue.
    private async void OnInstallAgainClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InstallItem { IsInstalled: true } item })
            return;
        if (_installCts != null)
        {
            SetStatus("An install is running — wait for it to finish.", WarnBrush);
            return;
        }
        await InstallQueueAsync(only: item);
    }

    // "new app" vs "update — installed v1.2", based on the last device scan.
    private void UpdateExistingLabels()
    {
        foreach (var item in _queue)
        {
            var installed = _apps.FirstOrDefault(a => a.Package == item.Package);
            item.ExistingText = installed == null
                ? (_apps.Count > 0 ? "new app" : "")
                : string.IsNullOrEmpty(installed.VersionText)
                    ? "update — already installed"
                    : "update — installed " + installed.VersionText;
        }
    }

    private void UpdateQueueUi()
    {
        bool running = _installCts != null;
        var pending = _queue.Where(i => i.State is InstallState.Waiting or InstallState.Failed or InstallState.Canceled).ToList();
        InstallAllBtn.IsEnabled = !running && pending.Count > 0 && _adb.IsReady;
        InstallAllBtn.Content = pending.Count > 0 && pending.All(i => i.State != InstallState.Waiting)
            ? "Retry"
            : pending.Count > 1 ? $"Install {pending.Count}" : "Install";
        CancelInstallBtn.IsVisible = running;
        UpdateDeviceButtons();
        ClearDoneBtn.IsEnabled = _queue.Any(i => i.State == InstallState.Done);
        GrantCheck.IsEnabled = !running;

        if (_queue.Count == 0)
        {
            QueueSummary.Text = "Queue is empty";
            return;
        }
        var bytes = pending.Sum(i => i.TotalBytes);
        var done = _queue.Count(i => i.State == InstallState.Done);
        QueueSummary.Text = $"{_queue.Count} in queue • {pending.Count} to install ({InstallItem.FmtBytes(bytes)})" +
                            (done > 0 ? $" • {done} installed" : "");
    }

    private async Task InstallQueueAsync(InstallItem? only = null)
    {
        if (_installCts != null)
            return;

        if (await EnsureConnectedAsync() == null)
            return;

        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;
        bool grant = GrantCheck.IsChecked == true;
        RefreshBtn.IsEnabled = false;
        int ok = 0, failed = 0;

        var batch = only != null
            ? new List<InstallItem> { only }
            : _queue.Where(i => i.State is InstallState.Waiting or InstallState.Failed or InstallState.Canceled).ToList();
        foreach (var item in batch)
            item.SetWaiting();
        UpdateQueueUi();

        try
        {
            for (int n = 0; n < batch.Count; n++)
            {
                var item = batch[n];
                if (!_queue.Contains(item))
                    continue;
                if (ct.IsCancellationRequested)
                {
                    item.SetCanceled();
                    continue;
                }
                SetStatus($"Installing {n + 1}/{batch.Count}: {item.DisplayName}…", InfoBrush);
                // the cable may have been unplugged since the last item — continue over Wi-Fi
                var serialBefore = _adb.Serial;
                if (await EnsureConnectedAsync() == null)
                {
                    item.SetFailed("Headset not connected.");
                    failed++;
                    continue;
                }
                var installed = await InstallOneAsync(item, grant, ct);
                // connection switched (cable unplugged) during the item: try once more on the new one
                if (!installed && item.State == InstallState.Failed && !ct.IsCancellationRequested &&
                    await EnsureConnectedAsync() != null && _adb.Serial != serialBefore)
                {
                    SetStatus($"Connection changed — retrying {item.DisplayName} over {Connection()}…", InfoBrush);
                    installed = await InstallOneAsync(item, grant, ct);
                }
                if (installed)
                    ok++;
                else if (item.State == InstallState.Failed)
                    failed++;
                UpdateQueueUi();
            }
        }
        finally
        {
            _installCts.Dispose();
            _installCts = null;
            UpdateQueueUi();
        }

        var summary = $"Installed {ok} of {batch.Count}" + (failed > 0 ? $", {failed} failed" : "") +
                      (ct.IsCancellationRequested ? " (canceled)" : "") + ". Refreshing app list…";
        SetStatus(summary, failed > 0 ? WarnBrush : OkBrush);
        if (ok > 0)
            await RefreshAsync();
        else
            RefreshBtn.IsEnabled = _adb.IsReady;
    }

    private string Connection() =>
        _adb.Serial != null && AdbService.IsWifiSerial(_adb.Serial) ? "Wi-Fi" : "USB";

    private async Task<bool> InstallOneAsync(InstallItem item, bool grant, CancellationToken ct)
    {
        try
        {
            item.SetRunning("Checking free space…");
            var (totalKb, usedKb) = await Task.Run(_adb.GetStorage);
            long freeBytes = (totalKb - usedKb) * 1024;
            // the APK is staged in /data/local/tmp before install, so it briefly needs twice its size
            long needBytes = item.ApkBytes * 2 + item.ObbBytes;
            if (needBytes > freeBytes)
            {
                item.SetFailed($"Not enough space: needs about {InstallItem.FmtBytes(needBytes)}, " +
                               $"free {InstallItem.FmtBytes(freeBytes)}. Uninstall something first.");
                return false;
            }

            bool downgrade = false;
            for (int attempt = 0; ; attempt++)
            {
                item.SetRunning("Uploading APK…", indeterminate: false);
                var result = await Task.Run(() => _adb.InstallApk(item.ApkPath, grant, downgrade,
                    (done, total) => Dispatcher.UIThread.Post(() => item.SetProgress("Uploading APK", done, total)),
                    () => Dispatcher.UIThread.Post(() => item.SetRunning("Installing (the headset verifies the APK)…")),
                    ct), ct);
                if (result.Success)
                    break;

                var reason = InstallScanner.Explain(result.Code, result.Output);
                if (attempt == 0 && result.Code == "INSTALL_FAILED_VERSION_DOWNGRADE" &&
                    await ConfirmDialog.AskCustomAsync(this, $"Downgrade “{item.DisplayName}”?",
                        $"A newer version is already installed on the headset.\n\nInstall {item.VersionText} anyway? " +
                        "Saved data is kept, but the app may not read data written by the newer version.",
                        "Downgrade"))
                {
                    downgrade = true;
                    continue;
                }
                if (attempt == 0 && result.Code == "INSTALL_FAILED_UPDATE_INCOMPATIBLE" && item.Package != null &&
                    await ConfirmDialog.AskCustomAsync(this, $"Replace “{item.DisplayName}”?",
                        "The installed copy is signed with a different key, so it cannot be updated in place.\n\n" +
                        "Uninstall it first and then install this APK? The app's saved data on the headset " +
                        "will be deleted.",
                        "Uninstall & install"))
                {
                    item.SetRunning("Uninstalling the old version…");
                    var package = item.Package;
                    if (!await Task.Run(() => _adb.Uninstall(package)))
                    {
                        item.SetFailed("Could not uninstall the old version (system refused).");
                        return false;
                    }
                    continue;
                }
                item.SetFailed(reason);
                return false;
            }

            long obbDone = 0;
            for (int i = 0; i < item.ObbFiles.Count; i++)
            {
                var obb = item.ObbFiles[i];
                var remote = $"/sdcard/Android/obb/{item.Package}/{Path.GetFileName(obb)}";
                var phase = item.ObbFiles.Count > 1 ? $"Copying OBB {i + 1}/{item.ObbFiles.Count}" : "Copying OBB";
                long before = obbDone;
                item.SetRunning(phase + "…", indeterminate: false);
                await Task.Run(() => _adb.Push(obb, remote,
                    (done, _) => Dispatcher.UIThread.Post(() => item.SetProgress(phase, before + done, item.ObbBytes)),
                    ct), ct);
                obbDone += new FileInfo(obb).Length;
            }

            item.SetDone(item.ObbFiles.Count > 0
                ? $"Installed + {item.ObbFiles.Count} OBB file(s) copied"
                : "Installed");
            return true;
        }
        catch (OperationCanceledException)
        {
            item.SetCanceled();
            return false;
        }
        catch (Exception ex)
        {
            item.SetFailed("Error: " + ex.Message);
            return false;
        }
    }

    // Search box: matches the app name or the package name, case-insensitive.
    private static List<QuestApp> Search(List<QuestApp> list, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
            return list;
        return list.Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               a.Package.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string ListSummary(List<QuestApp> all, List<QuestApp> shown)
    {
        if (all.Count == 0)
            return "—";
        if (shown.Count == all.Count)
            return $"{all.Count} apps • {Fmt(FmtTotal(all))} total";
        return shown.Count == 0
            ? $"No matches • {all.Count} apps in total"
            : $"{shown.Count} of {all.Count} apps • {Fmt(FmtTotal(shown))}";
    }

    private static long FmtTotal(List<QuestApp> list) =>
        list.Sum(a => Math.Max(a.SizeKb, 0));

    private static string Fmt(long kb) =>
        kb >= 1024 * 1024 ? $"{kb / 1048576.0:0.0} GB" : $"{kb / 1024.0:0.0} MB";

    private void SetStatus(string text, IBrush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }
}

internal sealed class ConfirmDialog : Window
{
    public static Task<bool> AskAsync(Window owner, string name, string package, string size) =>
        Show(owner,
            $"Uninstall “{name}”?",
            $"Package: {package}\nSize: {size}\n\nThis cannot be undone — the app would need to be installed again.",
            "Uninstall");

    public static Task<bool> AskFileAsync(Window owner, string name, string path, string size) =>
        Show(owner,
            $"Delete “{name}”?",
            $"Path: {path}\nSize: {size}\n\nThis cannot be undone. If this file belongs to an app, " +
            "the app may need to re-download it (or may stop working correctly).",
            "Delete");

    public static Task<bool> AskCustomAsync(Window owner, string heading, string details, string confirmText,
        string cancelText = "Cancel") =>
        Show(owner, heading, details, confirmText, cancelText);

    private static Task<bool> Show(Window owner, string heading, string details, string confirmText,
        string cancelText = "Cancel")
    {
        var dlg = new ConfirmDialog(heading, details, confirmText, cancelText);
        return dlg.ShowDialog<bool>(owner);
    }

    private ConfirmDialog(string heading, string details, string confirmText, string cancelText)
    {
        Title = "Please confirm";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(22), Spacing = 12 };

        root.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(new TextBlock
        {
            Text = details,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FF8B8CA0")),
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var cancel = new Button { Content = cancelText, MinWidth = 100 };
        var del = new Button
        {
            Content = confirmText,
            MinWidth = 100,
            Background = new SolidColorBrush(Color.Parse("#FF7A2C3A")),
            Foreground = Brushes.White,
        };
        cancel.Click += (_, _) => Close(false);
        del.Click += (_, _) => Close(true);
        row.Children.Add(cancel);
        row.Children.Add(del);
        root.Children.Add(row);

        Content = root;
    }
}
