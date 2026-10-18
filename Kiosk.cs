using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QuestApps;

// Kiosk for one headset: the app is pinned (no other app can be opened), the Meta menu
// (button or hand gesture — it cannot be blocked without MDM) is closed by bringing the app
// back within about a second, and a quit app is started and pinned again.
// Runs on its own background loop; follows the headset from USB to Wi-Fi and back.
public sealed class KioskGuard
{
    public string HeadsetId { get; }
    public string Package { get; }
    public int Returns { get; private set; }      // how often the app had to be brought back
    public int Restarts { get; private set; }     // how often it had to be started again

    // (guard, message, isWarning) — raised on a background thread
    public event Action<KioskGuard, string, bool>? Status;

    private readonly AdbService _adb;
    internal AdbService Adb => _adb;
    private readonly string? _ip;
    private readonly CancellationTokenSource _cts = new();
    private int _awayTicks;
    private DateTime _lastBring = DateTime.MinValue;
    private DateTime _lastResolve = DateTime.MinValue;

    internal KioskGuard(AdbService adb, string headsetId, string? ip, string package)
    {
        _adb = adb;
        HeadsetId = headsetId;
        _ip = ip;
        Package = package;
    }

    private Task _loop = Task.CompletedTask;

    internal void Start() => _loop = Task.Run(LoopAsync);
    internal void Stop() => _cts.Cancel();

    // Stop and wait until no tick is in flight, so it cannot pin the app again afterwards.
    internal void StopAndWait()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(10)); }
        catch { }
    }
    public bool IsStopped => _cts.IsCancellationRequested;

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch
            {
                // lost the connection (cable unplugged, Wi-Fi hiccup) — find the headset again
                if (DateTime.Now - _lastResolve > TimeSpan.FromSeconds(3))
                {
                    _lastResolve = DateTime.Now;
                    try { _adb.Serial = DeviceDiscovery.ResolveSerial(_adb, HeadsetId, _ip) ?? _adb.Serial; }
                    catch { }
                }
            }
            try { await Task.Delay(600, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var snap = _adb.GetKioskSnapshot(Package);
        ct.ThrowIfCancellationRequested();

        if (!snap.Running)
        {
            _awayTicks = 0;
            Restarts++;
            Report("was closed — starting it again…", true);
            _adb.Launch(Package);
            await Task.Delay(3000, ct);
            var error = _adb.LockApp(Package);
            Report(error == null ? "restarted and locked again." : $"restarted, but locking failed ({error}) — will retry.",
                error != null);
            return;
        }

        if (snap.Foreground != null && snap.Foreground != Package)
        {
            // something else is on top (Meta menu / home): bring the app back about once a
            // second — slower if it keeps losing (e.g. the headset went to sleep)
            _awayTicks++;
            var every = TimeSpan.FromSeconds(_awayTicks > 15 ? 3 : 1);
            if (DateTime.Now - _lastBring >= every)
            {
                _lastBring = DateTime.Now;
                _adb.BringToFront(Package);
                Returns++;
                Report($"brought back on top ({Returns}× so far) — the Meta menu was opened.", true);
            }
            if (_awayTicks % 5 == 0)
                _adb.LockApp(Package); // pin again in case the pin moved
            return;
        }

        _awayTicks = 0;
        if (!snap.Pinned && _adb.LockApp(Package) == null)
            Report("the lock was lifted in the headset — locked again.", true);
    }

    private void Report(string message, bool warning)
    {
        if (!IsStopped)
            Status?.Invoke(this, message, warning);
    }
}

// All active kiosks, shared by the main window and the multi-headset window,
// so both see (and end) the same lock.
public static class KioskRegistry
{
    private static readonly Dictionary<string, KioskGuard> Guards = new(StringComparer.Ordinal);

    // Raised (on any thread) when a kiosk starts or ends.
    public static event Action? Changed;
    // Forwarded from every guard: (guard, message, isWarning), on a background thread.
    public static event Action<KioskGuard, string, bool>? Status;

    public static KioskGuard? Get(string? headsetId)
    {
        if (headsetId == null)
            return null;
        lock (Guards)
            return Guards.TryGetValue(headsetId, out var g) ? g : null;
    }

    public static List<KioskGuard> All()
    {
        lock (Guards)
            return Guards.Values.ToList();
    }

    // Pins the app (it must be running) and starts guarding it. Null on success, else a reason.
    // Blocking — call off the UI thread.
    public static string? Lock(AdbService baseAdb, string headsetId, string serial, string? ip, string package)
    {
        var adb = baseAdb.ForSerial(serial);
        var error = adb.LockApp(package);
        if (error != null)
            return error;
        Watch(adb, headsetId, ip, package);
        return null;
    }

    // Guards a lock that is already in place (e.g. found on the headset at startup).
    public static void Adopt(AdbService baseAdb, string headsetId, string serial, string? ip, string package)
    {
        var existing = Get(headsetId);
        if (existing?.Package == package)
            return;
        Watch(baseAdb.ForSerial(serial), headsetId, ip, package);
    }

    // Stops guarding and unpins. Blocking — call off the UI thread.
    public static void Unlock(AdbService baseAdb, string headsetId, string? serial)
    {
        KioskGuard? guard;
        lock (Guards)
        {
            Guards.Remove(headsetId, out guard);
        }
        guard?.StopAndWait();
        Changed?.Invoke();
        if (serial != null)
            baseAdb.ForSerial(serial).UnlockApp();
    }

    // Ends every kiosk (window closing). Blocking.
    public static void UnlockAll()
    {
        List<KioskGuard> guards;
        lock (Guards)
        {
            guards = Guards.Values.ToList();
            Guards.Clear();
        }
        Parallel.ForEach(guards, g =>
        {
            g.StopAndWait();
            try { g.Adb.UnlockApp(); }
            catch { /* headset gone — nothing to unlock */ }
        });
        Changed?.Invoke();
    }

    private static void Watch(AdbService adb, string headsetId, string? ip, string package)
    {
        var guard = new KioskGuard(adb, headsetId, ip, package);
        guard.Status += (g, m, w) => Status?.Invoke(g, m, w);
        lock (Guards)
        {
            if (Guards.Remove(headsetId, out var old))
                old.Stop();
            Guards[headsetId] = guard;
        }
        guard.Start();
        Changed?.Invoke();
    }
}
