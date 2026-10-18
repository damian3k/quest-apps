using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Media;

namespace QuestApps;

public sealed class QuestApp : INotifyPropertyChanged
{
    public string Package { get; }
    public string Installer { get; }
    public long SizeKb { get; }
    public string ApkPath { get; }

    public QuestApp(string package, string installer, long sizeKb, string apkPath)
    {
        Package = package;
        Installer = installer;
        SizeKb = sizeKb;
        ApkPath = apkPath;
    }

    public bool IsStore => Installer.StartsWith("com.oculus", StringComparison.OrdinalIgnoreCase);

    public string SizeText => SizeKb < 0
        ? "?"
        : SizeKb >= 1024 * 1024 ? $"{SizeKb / 1048576.0:0.0} GB" : $"{SizeKb / 1024.0:0.0} MB";

    public string SourceText => Package + " — " + (IsStore
        ? "official, Meta Store"
        : Installer == "null" ? "sideloaded via adb" : "sideloaded, installer: " + Installer);

    private string? _title;
    public string? Title
    {
        get => _title;
        set { _title = value; Raise(nameof(Title)); Raise(nameof(DisplayName)); }
    }

    public string DisplayName => Title ?? Package;

    private string? _version;
    public string? Version
    {
        get => _version;
        set { _version = value; Raise(nameof(Version)); Raise(nameof(VersionText)); }
    }

    public string VersionText => string.IsNullOrEmpty(Version) ? "" : "v" + Version;

    private IImage? _icon;
    public IImage? Icon
    {
        get => _icon;
        set { _icon = value; Raise(nameof(Icon)); }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            RaiseRunState();
        }
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            RaiseRunState();
        }
    }

    public bool CanLaunch => !IsRunning;
    public bool CanStop => IsRunning && !IsLocked;
    public bool CanLock => IsRunning && !IsLocked;
    public string RunStateText => IsLocked ? "🔒 locked" : IsRunning ? "● running" : "";

    private void RaiseRunState()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsLocked));
        Raise(nameof(CanLaunch));
        Raise(nameof(CanStop));
        Raise(nameof(CanLock));
        Raise(nameof(RunStateText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record AdbDevice(string Serial, string State, string Model);

// Result of `pm install`: Code is the INSTALL_FAILED_* token when the system refused.
public sealed record InstallResult(bool Success, string? Code, string Output);

public sealed record LargeFile(string Path, long SizeKb)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string Ext => System.IO.Path.GetExtension(Name);
    public string Dir => Path[..(Path.LastIndexOf('/') + 1)];
    public string SizeText => SizeKb < 0
        ? "?"
        : SizeKb >= 1024 * 1024 ? $"{SizeKb / 1048576.0:0.0} GB" : $"{SizeKb / 1024.0:0.0} MB";
}

public sealed class AdbService
{
    public bool IsReady { get; private set; }
    public string AdbPath { get; private set; } = "";
    public string LastError { get; private set; } = "";

    // Device every command goes to. Required once the headset is visible twice
    // (USB + Wi-Fi), otherwise adb refuses with "more than one device".
    public string? Serial { get; set; }

    public static bool IsWifiSerial(string serial) => serial.Contains(':');

    // A separate handle bound to one device, so several headsets can be driven in parallel.
    public AdbService ForSerial(string? serial) =>
        new() { AdbPath = AdbPath, IsReady = IsReady, Serial = serial };

    private static string AdbFileName => OperatingSystem.IsWindows() ? "adb.exe" : "adb";

    // adb shipped next to the app (adb\adb.exe on Windows, adb/adb on macOS and Linux)
    public static string BundledAdb => Path.Combine(AppContext.BaseDirectory, "adb", AdbFileName);

    public bool TrySetAdb(string path)
    {
        IsReady = false;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
            if (!File.Exists(path))
            {
                LastError = "file does not exist: " + path;
                return false;
            }
            EnsureExecutable(path);
            AdbPath = path;
            var v = RunHost("version");
            if (string.IsNullOrWhiteSpace(v) || v.IndexOf("Android Debug Bridge", StringComparison.OrdinalIgnoreCase) < 0)
            {
                LastError = "this does not look like adb";
                return false;
            }
            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    // Archives and copies can drop the executable bit on macOS/Linux.
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, mode | exec);
        }
        catch { /* read-only location — adb may still run */ }
    }

    public static IEnumerable<string> FindAdbCandidates()
    {
        var found = new List<string> { BundledAdb };
        found.AddRange(OperatingSystem.IsWindows() ? WindowsAdbCandidates() : UnixAdbCandidates());
        return found.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static IEnumerable<string> WindowsAdbCandidates()
    {
        var found = new List<string>();
        try
        {
            var where = RunHiddenText("where.exe", "adb", 5);
            var first = where.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            if (first != null && File.Exists(first))
                found.Add(first);
        }
        catch { }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fixedPaths = new[]
        {
            Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"),
            Path.Combine(local, "SideQuest", "adb.exe"),
            @"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe",
            @"C:\adb\platform-tools\adb.exe",
        };
        found.AddRange(fixedPaths.Where(File.Exists));

        foreach (var root in new[] { Path.Combine(local, "SideQuest"), Path.Combine(local, "Programs", "SideQuest") })
        {
            try
            {
                if (Directory.Exists(root))
                    found.AddRange(Directory.EnumerateFiles(root, "adb.exe", SearchOption.AllDirectories).Take(3));
            }
            catch { }
        }
        return found;
    }

    // macOS / Linux. Apps started from Finder or a desktop menu get a minimal PATH,
    // so the usual install locations (Homebrew, Android Studio, distro packages) are listed too.
    private static IEnumerable<string> UnixAdbCandidates()
    {
        var found = new List<string>();
        try
        {
            var which = RunHiddenText("/bin/sh", "-c \"command -v adb\"", 5).Trim();
            if (which.Length > 0 && File.Exists(which))
                found.Add(which);
        }
        catch { }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fixedPaths = new[]
        {
            Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb"), // Android Studio, macOS
            Path.Combine(home, "Android", "Sdk", "platform-tools", "adb"),            // Android Studio, Linux
            Path.Combine(home, "platform-tools", "adb"),
            "/opt/homebrew/bin/adb",                                                  // Homebrew, Apple Silicon
            "/usr/local/bin/adb",                                                     // Homebrew, Intel
            "/usr/bin/adb",                                                           // apt / dnf / pacman
            "/opt/android-sdk/platform-tools/adb",
            "/Applications/SideQuest.app/Contents/Resources/app.asar.unpacked/build/platform-tools/adb",
        };
        found.AddRange(fixedPaths.Where(File.Exists));
        return found;
    }

    public List<AdbDevice> ListDevices()
    {
        var devices = new List<AdbDevice>();
        var output = RunHost("devices -l");
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("*") ||
                line.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] is not ("device" or "unauthorized" or "offline"))
                continue;

            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal));
            devices.Add(new AdbDevice(parts[0], parts[1], model?["model:".Length..] ?? "?"));
        }
        return devices;
    }

    public (long TotalKb, long UsedKb) GetStorage()
    {
        var output = Run("shell df /data");
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("/data", StringComparison.Ordinal) && !line.StartsWith("/dev/block", StringComparison.Ordinal))
                continue;

            // toybox: "/data 118183528 96365444 21818084 82% /data"
            var toks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length >= 3 && long.TryParse(toks[1], out var total) && long.TryParse(toks[2], out var used))
                return (total, used);

            // legacy: "/data: 115413MB total, 94105MB used, ..."
            var m = Regex.Match(line, @"(\d+)MB total.*?(\d+)MB used");
            if (m.Success)
                return (long.Parse(m.Groups[1].Value) * 1024, long.Parse(m.Groups[2].Value) * 1024);
        }
        throw new InvalidOperationException("failed to read df /data: " + output.Replace("\n", " | "));
    }

    public bool Uninstall(string package)
    {
        var output = Run($"shell pm uninstall {package}");
        return output.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Files >= 50 MB anywhere in shared storage (/sdcard) — mostly app data
    // in Android/data and obb, downloads and recorded media. Read-only scan.
    // NOTE: toybox find/du refuse to traverse /sdcard itself on Quest (silent),
    // but work when started from its children — hence the /* glob.
    public List<LargeFile> GetLargeFiles()
    {
        var output = Run("shell du -a -k /sdcard/* 2>/dev/null");
        var all = new List<(string Path, long Kb)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("du:"))
                continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line[..tab], out var kb))
                continue;
            var path = line[(tab + 1)..].Trim();
            if (path.Length > 0)
                all.Add((path, kb));
        }

        // du -a lists directories too (as sums of children) — mark every path
        // that is an ancestor of another path, and keep files only.
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, _) in all)
        {
            int idx = 0;
            while ((idx = path.IndexOf('/', idx + 1)) > 0)
                dirs.Add(path[..idx]);
        }

        return all
            .Where(e => e.Kb >= 51200 && !dirs.Contains(e.Path))
            .Select(e => new LargeFile(e.Path, e.Kb))
            .OrderByDescending(f => f.SizeKb)
            .ToList();
    }

    public bool DeleteFile(string path)
    {
        Run($"shell rm \"{path}\"");
        // verify it is really gone
        var check = Run($"shell ls \"{path}\" 2>/dev/null");
        return check.Trim().Length == 0 || check.Contains("No such file", StringComparison.OrdinalIgnoreCase);
    }

    public List<QuestApp> GetApps()
    {
        // One call: APK path + package + installer for every user app.
        var entries = new List<(string Pkg, string Installer, string Dir, string Apk)>();
        var listing = Run("shell pm list packages -3 -f -i");
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:"))
                continue;

            var body = line["package:".Length..];
            var installer = "null";
            var left = body;
            var i = body.IndexOf("installer=", StringComparison.Ordinal);
            if (i >= 0)
            {
                installer = body[(i + "installer=".Length)..].Trim();
                left = body[..i].Trim();
            }

            var eq = left.LastIndexOf('=');
            if (eq < 0)
                continue;
            var apk = left[..eq];
            var pkg = left[(eq + 1)..];
            if (pkg.Length == 0 || apk.Length == 0)
                continue;

            var slash = apk.LastIndexOf('/');
            entries.Add((pkg, installer, slash > 0 ? apk[..slash] : apk, apk));
        }

        // One du call per 40 APK dirs instead of one per app.
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var chunk in entries.Select(e => e.Dir).Distinct().Chunk(40))
        {
            var duOut = Run("shell du -k -s " + string.Join(" ", chunk));
            foreach (var raw in duOut.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("du:"))
                    continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line[..tab], out var kb))
                    continue;
                sizes[line[(tab + 1)..].Trim()] = kb;
            }
        }

        // External app data (/sdcard/Android/{data,obb,media}) — readable by shell,
        // attributed to the package by directory name.
        var ext = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var root in new[] { "/sdcard/Android/data", "/sdcard/Android/obb", "/sdcard/Android/media" })
        {
            string duOut;
            try { duOut = Run($"shell du -k -s {root}/*"); }
            catch { continue; }
            foreach (var raw in duOut.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("du:"))
                    continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0 || !long.TryParse(line[..tab], out var kb))
                    continue;
                var path = line[(tab + 1)..].Trim();
                var pkg = path[(path.LastIndexOf('/') + 1)..];
                if (pkg.Length == 0 || pkg.StartsWith('.'))
                    continue;
                ext.TryGetValue(pkg, out var cur);
                ext[pkg] = cur + kb;
            }
        }

        return entries
            .Select(e =>
            {
                var apkKnown = sizes.TryGetValue(e.Dir, out var apk);
                var extKnown = ext.TryGetValue(e.Pkg, out var ex);
                long total = (apkKnown ? Math.Max(apk, 0) : 0) + (extKnown ? Math.Max(ex, 0) : 0);
                if (!apkKnown && !extKnown)
                    total = -1;
                return new QuestApp(e.Pkg, e.Installer, total, e.Apk);
            })
            .OrderByDescending(a => a.SizeKb)
            .ToList();
    }

    private const string TmpApk = "/data/local/tmp/questapps-install.apk";

    // Upload the APK to a temp file (with progress), install it with pm, remove the temp file.
    // onInstalling fires when the upload is done and the package manager takes over.
    public InstallResult InstallApk(string localApk, bool grantAll, bool allowDowngrade,
        Action<long, long>? progress, Action? onInstalling, CancellationToken ct)
    {
        try
        {
            Push(localApk, TmpApk, progress, ct);
            onInstalling?.Invoke();
            // -r replace existing, -t allow test-only builds (common for sideloaded dev APKs)
            var flags = "-r -t" + (grantAll ? " -g" : "") + (allowDowngrade ? " -d" : "");
            var output = RunLong(new[] { "shell", $"pm install {flags} {TmpApk}" }, TimeSpan.FromMinutes(15), ct);
            if (output.Contains("Success", StringComparison.Ordinal))
                return new InstallResult(true, null, output);
            var m = Regex.Match(output, @"\b(INSTALL_[A-Z_]+)");
            return new InstallResult(false, m.Success ? m.Groups[1].Value : null, output.Trim());
        }
        finally
        {
            try { Run("shell rm -f " + TmpApk); } catch { }
        }
    }

    // adb push writes straight into the target file, so polling its size on the
    // device gives real progress (adb itself prints none when not on a terminal).
    public void Push(string localPath, string remotePath, Action<long, long>? progress, CancellationToken ct)
    {
        long total = new FileInfo(localPath).Length;
        var remote = ShQuote(remotePath);
        Run("shell mkdir -p " + ShQuote(remotePath[..remotePath.LastIndexOf('/')]));

        using var p = StartAdb(new[] { "push", localPath, remotePath });
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        progress?.Invoke(0, total);
        while (!p.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                try { Run("shell rm -f " + remote); } catch { }
                throw new OperationCanceledException(ct);
            }
            if (progress != null && long.TryParse(TryRun("shell stat -c %s " + remote).Trim(), out var done))
                progress(Math.Min(done, total), total);
        }

        var output = (stdout.Result + "\n" + stderr.Result).Trim();
        if (p.ExitCode != 0)
            throw new IOException("adb push failed: " + LastLine(output));
        var size = Run("shell stat -c %s " + remote).Trim();
        if (!long.TryParse(size, out var pushed) || pushed != total)
            throw new IOException($"copy incomplete ({size} of {total} bytes)");
        progress?.Invoke(total, total);
    }

    private string RunLong(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        using var p = StartAdb(args);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow + timeout;
        while (!p.WaitForExit(250))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                try { p.Kill(); } catch { }
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                throw new TimeoutException($"adb {string.Join(' ', args)} — timed out");
            }
        }
        return stdout.Result + "\n" + stderr.Result;
    }

    private Process StartAdb(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (Serial != null)
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(Serial);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start adb");
    }

    // ---- Wi-Fi ADB ----
    public const int WifiPort = 5555;

    // IPv4 of the headset's Wi-Fi interface (asked over the current connection, usually USB).
    public string? GetWifiIp(string serial)
    {
        var output = RunOn(serial, "shell ip -f inet addr show wlan0");
        var m = Regex.Match(output, @"inet (\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Is adbd on the device already listening on TCP (enabled since the last reboot)?
    public bool IsTcpipEnabled(string serial) =>
        RunOn(serial, "shell getprop service.adb.tcp.port").Trim() == WifiPort.ToString();

    // Switch adbd on the device to listen on TCP; it restarts, so USB drops for a moment.
    public void EnableTcpip(string serial) => RunOn(serial, $"tcpip {WifiPort}");

    public bool Connect(string ip)
    {
        var output = RunHost($"connect {ip}:{WifiPort}");
        return output.Contains("connected to", StringComparison.OrdinalIgnoreCase);
    }

    public void Disconnect(string serial) => RunHost($"disconnect {serial}");

    // Quick TCP probe, so an absent headset costs ~1.5 s instead of adb connect's long timeout.
    public static bool CanReach(string ip)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(ip, WifiPort).Wait(1500) && client.Connected;
        }
        catch { return false; }
    }

    // Command for a specific device, independent of Serial (safe from background polling).
    public string RunOn(string serial, string args) => RunHiddenText(AdbPath, $"-s {serial} {args}", 30);

    // ---- running apps, kiosk ----

    // Package names with a live process (":service" sub-processes count as their app).
    public HashSet<string> GetRunningPackages()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Run("shell ps -A -o NAME").Split('\n'))
        {
            var name = raw.Trim();
            var colon = name.IndexOf(':');
            if (colon > 0)
                name = name[..colon];
            if (name.Contains('.'))
                result.Add(name);
        }
        return result;
    }

    public void ForceStop(string package) => Run($"shell am force-stop {package}");

    // Screen pinning ("lock task"): the system refuses to switch away from the task.
    public string? LockApp(string package)
    {
        var activities = Run("shell dumpsys activity activities");
        // ActivityRecord{2bfaa4f u0 com.pkg/com.pkg.MainActivity t9370}
        var m = Regex.Match(activities, @"u0 " + Regex.Escape(package) + @"/\S+ t(\d+)\}");
        if (!m.Success)
            return "the app has no open window (is it running?)";
        Run($"shell am task lock {m.Groups[1].Value}");
        return GetLockInfo().Package == package ? null : "the system refused to lock the app";
    }

    public void UnlockApp() => Run("shell am task lock stop");

    // One quick call (~150 ms) for the kiosk watchdog: is the app alive, which app is
    // on top (the Meta menu shows up as com.oculus.vrshell), and is a task still pinned.
    public (bool Running, string? Foreground, bool Pinned) GetKioskSnapshot(string package)
    {
        var output = Run($"shell \"pidof {package}; dumpsys activity activities | " +
                         "grep -E 'ResumedActivity: |mLockTaskModeState'\"");
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        bool running = lines.Count > 0 && Regex.IsMatch(lines[0], @"^\d+");
        var top = Regex.Match(output, @"ResumedActivity: ActivityRecord\{\S+ u0 ([\w.]+)/");
        var state = Regex.Match(output, @"mLockTaskModeState=(\w+)");
        return (running, top.Success ? top.Groups[1].Value : null,
                state.Success && state.Groups[1].Value != "NONE");
    }

    // Fast list of user apps (package -> APK path), without sizes.
    public Dictionary<string, string> ListUserPackages()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in Run("shell pm list packages -3 -f").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal))
                continue;
            var eq = line.LastIndexOf('=');
            if (eq > "package:".Length)
                result[line[(eq + 1)..]] = line["package:".Length..eq];
        }
        return result;
    }

    // One call for the multi-headset view: battery, charging, app on top, pinned.
    public (int? Battery, bool Charging, string? Foreground, bool Pinned) GetStatusSnapshot()
    {
        var output = Run("shell \"dumpsys battery | grep -E '^  (level|status):'; dumpsys activity activities | " +
                         "grep -E 'ResumedActivity: |mLockTaskModeState'\"");
        var level = Regex.Match(output, @"level: (\d+)");
        var status = Regex.Match(output, @"status: (\d+)");
        var top = Regex.Match(output, @"ResumedActivity: ActivityRecord\{\S+ u0 ([\w.]+)/");
        var state = Regex.Match(output, @"mLockTaskModeState=(\w+)");
        return (level.Success ? int.Parse(level.Groups[1].Value) : null,
                status.Success && status.Groups[1].Value is "2" or "5", // charging / full
                top.Success ? top.Groups[1].Value : null,
                state.Success && state.Groups[1].Value != "NONE");
    }

    // Brings the app's existing task back on top (am start reuses the running task).
    public void BringToFront(string package)
    {
        var component = ResolveLauncher(package);
        if (component != null)
            Run($"shell am start -n {component}");
    }

    private readonly Dictionary<string, string> _launchers = new(StringComparer.Ordinal);

    // pkg/activity of the launcher (or Oculus VR) activity, cached per package.
    private string? ResolveLauncher(string package)
    {
        lock (_launchers)
            if (_launchers.TryGetValue(package, out var cached))
                return cached;
        foreach (var category in new[] { "android.intent.category.LAUNCHER", "com.oculus.intent.category.VR" })
        {
            var resolved = Run("shell cmd package resolve-activity --brief " +
                               $"-a android.intent.action.MAIN -c {category} {package}");
            var component = resolved.Split('\n').Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith(package + "/", StringComparison.Ordinal));
            if (component == null)
                continue;
            lock (_launchers)
                _launchers[package] = component;
            return component;
        }
        return null;
    }

    // State is NONE, PINNED or LOCKED; Package is the app whose task is locked.
    public (string State, string? Package) GetLockInfo()
    {
        var dump = Run("shell dumpsys activity activities");
        var state = Regex.Match(dump, @"mLockTaskModeState=(\w+)");
        if (!state.Success || state.Groups[1].Value == "NONE")
            return ("NONE", null);
        //   mLockTaskModeTasks=
        //     #0 Task{999c5dc #9370 type=standard A=10199:com.VR.Goryl}
        var task = Regex.Match(dump, @"mLockTaskModeTasks=\s*#0 Task\{\S+ #(\d+)");
        string? package = null;
        if (task.Success)
        {
            var owner = Regex.Match(dump, @"u0 ([\w.]+)/\S+ t" + task.Groups[1].Value + @"\}");
            if (owner.Success)
                package = owner.Groups[1].Value;
        }
        return (state.Groups[1].Value, package);
    }

    // Quest: ignore the proximity sensor so the headset stays awake off the head
    // (keeps Wi-Fi ADB and kiosk apps alive). Reset by a reboot or by turning it off.
    public void SetKeepAwake(bool on) => Run(on
        ? "shell am broadcast -a com.oculus.vrpowermanager.prox_close"
        : "shell am broadcast -a com.oculus.vrpowermanager.automation_disable");

    public bool GetKeepAwake() =>
        Run("shell dumpsys vrpowermanager").Contains("Virtual proximity state: CLOSE", StringComparison.Ordinal);

    // ---- launching ----

    // Starts the app's launcher activity. Returns null on success or a reason.
    // VR-only apps on Quest may declare only the Oculus VR category instead of LAUNCHER.
    public string? Launch(string package)
    {
        var component = ResolveLauncher(package);
        if (component != null)
        {
            var output = Run($"shell am start -n {component}");
            return output.Contains("Error", StringComparison.Ordinal) ? LastLine(output) : null;
        }

        var monkey = Run($"shell monkey -p {package} -c android.intent.category.LAUNCHER 1");
        return monkey.Contains("No activities found", StringComparison.OrdinalIgnoreCase) ||
               monkey.Contains("aborted", StringComparison.OrdinalIgnoreCase)
            ? "this app has no launchable activity"
            : null;
    }

    // Single-quote for the device shell (adb joins shell args with spaces).
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string LastLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? s;

    private string TryRun(string args)
    {
        try { return Run(args); }
        catch { return ""; }
    }

    // Device command — goes to Serial.
    public string Run(string args) => RunHiddenText(AdbPath, SerialPrefix + args, 30);

    // Host command (version, devices, connect, disconnect) — no device selection.
    public string RunHost(string args) => RunHiddenText(AdbPath, args, 30);

    private string SerialPrefix => Serial == null ? "" : $"-s {Serial} ";

    public byte[] RunBinary(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            Arguments = SerialPrefix + args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start adb");
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        if (!p.WaitForExit(30000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"adb {args} — timed out");
        }
        _ = stderrTask.Result;
        return ms.ToArray();
    }

    private static string RunHiddenText(string exe, string args, int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start: " + exe);
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"{Path.GetFileName(exe)} {args} — timed out after {timeoutSec}s");
        }
        _ = stderrTask.Result;
        return stdout;
    }
}
