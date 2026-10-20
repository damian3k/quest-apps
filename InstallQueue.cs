using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace QuestApps;

public enum InstallState { Waiting, Running, Done, Failed, Canceled }

public sealed class InstallItem : INotifyPropertyChanged
{
    private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#FF8B8CA0"));
    private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#FF57D98A"));
    private static readonly IBrush BusyBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6C93DD"));
    private static readonly IBrush ErrBrush = new ImmutableSolidColorBrush(Color.Parse("#FFFF6B6B"));

    public string ApkPath { get; }
    public long ApkBytes { get; }
    public string? Package { get; }
    public string? Title { get; }
    public string? Version { get; }
    internal byte[]? IconPng { get; }
    public IReadOnlyList<string> ObbFiles { get; }
    public long ObbBytes { get; }

    internal InstallItem(string apkPath, ApkInfo? info, IReadOnlyList<string> obbFiles)
    {
        ApkPath = apkPath;
        ApkBytes = new FileInfo(apkPath).Length;
        Package = info?.Package;
        Title = info?.Label;
        Version = info?.VersionName;
        IconPng = info?.IconPng;
        ObbFiles = obbFiles;
        ObbBytes = obbFiles.Sum(f => new FileInfo(f).Length);
    }

    public string DisplayName => Title ?? Package ?? Path.GetFileName(ApkPath);
    public string VersionText => string.IsNullOrEmpty(Version) ? "" : "v" + Version;
    public long TotalBytes => ApkBytes + ObbBytes;

    public string DetailsText
    {
        get
        {
            var s = $"{Package ?? "?"} • APK {FmtBytes(ApkBytes)}";
            if (ObbFiles.Count > 0)
                s += $" • {ObbFiles.Count} OBB {FmtBytes(ObbBytes)}";
            return s + " • " + Path.GetFileName(ApkPath);
        }
    }

    private IImage? _icon;
    public IImage? Icon
    {
        get => _icon;
        set { _icon = value; Raise(nameof(Icon)); }
    }

    // "new app" / "update — installed v1.2", filled from the device's app list
    private string _existingText = "";
    public string ExistingText
    {
        get => _existingText;
        set { _existingText = value; Raise(nameof(ExistingText)); }
    }

    private InstallState _state;
    public InstallState State
    {
        get => _state;
        private set
        {
            _state = value;
            Raise(nameof(State));
            Raise(nameof(IsBusy));
            Raise(nameof(CanRemove));
            RaiseRunState();
        }
    }

    public bool IsBusy => State == InstallState.Running;
    public bool CanRemove => State != InstallState.Running;

    // once installed: Launch (turns into Stop while the app runs) and Install again
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

    public bool IsInstalled => State == InstallState.Done && Package != null;
    public bool CanLaunch => IsInstalled && !IsRunning;
    public bool CanStop => IsInstalled && IsRunning && !IsLocked;
    public string RunStateText => !IsInstalled ? "" : IsLocked ? "🔒 locked" : IsRunning ? "● running" : "";

    private void RaiseRunState()
    {
        Raise(nameof(IsInstalled));
        Raise(nameof(CanLaunch));
        Raise(nameof(CanStop));
        Raise(nameof(RunStateText));
    }

    private string _statusText = "Waiting";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Raise(nameof(StatusText)); }
    }

    private IBrush _statusBrush = InfoBrush;
    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; Raise(nameof(StatusBrush)); }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set { _progress = value; Raise(nameof(Progress)); }
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set { _isIndeterminate = value; Raise(nameof(IsIndeterminate)); }
    }

    private readonly Stopwatch _phaseClock = new();

    public void SetWaiting() => Set(InstallState.Waiting, "Waiting", InfoBrush, 0, false);

    public void SetRunning(string text, bool indeterminate = true)
    {
        _phaseClock.Restart();
        Set(InstallState.Running, text, BusyBrush, 0, indeterminate);
    }

    public void SetProgress(string phase, long done, long total)
    {
        var pct = total > 0 ? 100.0 * done / total : 0;
        var secs = _phaseClock.Elapsed.TotalSeconds;
        var speed = secs > 1 ? $" • {done / secs / 1048576.0:0.0} MB/s" : "";
        Set(InstallState.Running, $"{phase} {pct:0}% ({FmtBytes(done)} of {FmtBytes(total)}){speed}",
            BusyBrush, pct, false);
    }

    public void SetDone(string text) => Set(InstallState.Done, text, OkBrush, 100, false);
    public void SetFailed(string text) => Set(InstallState.Failed, text, ErrBrush, 0, false);
    public void SetCanceled() => Set(InstallState.Canceled, "Canceled", InfoBrush, 0, false);

    private void Set(InstallState state, string text, IBrush brush, double progress, bool indeterminate)
    {
        State = state;
        StatusText = text;
        StatusBrush = brush;
        Progress = progress;
        IsIndeterminate = indeterminate;
    }

    public static string FmtBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / 1048576.0:0.0} MB";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Turns dropped/picked paths into install items: APK files, folders with APKs,
// and OBB data matched to its APK by package name.
internal static class InstallScanner
{
    public static (List<InstallItem> Items, List<string> Skipped) Collect(IEnumerable<string> paths)
    {
        var apks = new List<string>();
        var looseObbs = new List<string>();
        var skipped = new List<string>();

        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                var found = FindApks(path);
                if (found.Count > 0)
                    apks.AddRange(found);
                else
                {
                    // e.g. a "com.studio.game" folder dropped next to its APK
                    var obbs = SafeFiles(path, "*.obb", SearchOption.TopDirectoryOnly);
                    if (obbs.Count > 0)
                        looseObbs.AddRange(obbs);
                    else
                        skipped.Add(Path.GetFileName(path) + " (no APK inside)");
                }
            }
            else if (HasExt(path, ".apk"))
                apks.Add(path);
            else if (HasExt(path, ".obb"))
                looseObbs.Add(path);
            else if (HasExt(path, ".apks") || HasExt(path, ".xapk") || HasExt(path, ".apkm"))
                skipped.Add(Path.GetFileName(path) + " (split-APK bundles are not supported)");
            else
                skipped.Add(Path.GetFileName(path) + " (not an APK)");
        }

        looseObbs = looseObbs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<InstallItem>();
        var usedObbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var apk in apks.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ApkInfo? info;
            try { info = ApkLabelReader.ReadInfo(new LocalApkSource(apk)); }
            catch { info = null; }
            if (info?.Package == null)
            {
                skipped.Add(Path.GetFileName(apk) + " (unreadable APK)");
                continue;
            }

            var obbs = FindObbs(Path.GetDirectoryName(apk)!, info.Package)
                .Concat(looseObbs.Where(o => ObbBelongsTo(o, info.Package)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            usedObbs.UnionWith(obbs);
            items.Add(new InstallItem(apk, info, obbs));
        }

        skipped.AddRange(looseObbs
            .Where(o => !usedObbs.Contains(o))
            .Select(o => Path.GetFileName(o) + " (OBB without a matching APK)"));

        return (items, skipped);
    }

    // APKs at the top of the folder, or one level down (a folder of game folders).
    private static List<string> FindApks(string dir)
    {
        var top = SafeFiles(dir, "*.apk", SearchOption.TopDirectoryOnly);
        if (top.Count > 0)
            return top;
        var result = new List<string>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
                result.AddRange(SafeFiles(sub, "*.apk", SearchOption.TopDirectoryOnly));
        }
        catch { }
        return result;
    }

    // Common layouts: <dir>/<package>/*.obb (SideQuest style) and
    // <dir>/main.<ver>.<package>.obb next to the APK.
    private static IEnumerable<string> FindObbs(string apkDir, string package)
    {
        var sub = Path.Combine(apkDir, package);
        if (Directory.Exists(sub))
            foreach (var f in SafeFiles(sub, "*.obb", SearchOption.TopDirectoryOnly))
                yield return f;
        foreach (var f in SafeFiles(apkDir, "*.obb", SearchOption.TopDirectoryOnly))
            if (NameMatches(f, package))
                yield return f;
    }

    private static bool ObbBelongsTo(string obb, string package) =>
        NameMatches(obb, package) ||
        string.Equals(Path.GetFileName(Path.GetDirectoryName(obb)), package, StringComparison.Ordinal);

    private static bool NameMatches(string obb, string package) =>
        Path.GetFileName(obb).EndsWith("." + package + ".obb", StringComparison.Ordinal);

    private static bool HasExt(string path, string ext) =>
        string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private static List<string> SafeFiles(string dir, string pattern, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, pattern, option).OrderBy(f => f).ToList(); }
        catch { return new List<string>(); }
    }

    // Human-readable reasons for the most common pm install failures.
    public static string Explain(string? code, string output)
    {
        var known = Known(code);
        return known != null ? $"{known} ({code})" : "Install failed: " + FirstUseful(output);
    }

    private static string? Known(string? code) => code switch
    {
        "INSTALL_FAILED_UPDATE_INCOMPATIBLE" =>
            "The installed version is signed with a different key.",
        "INSTALL_FAILED_VERSION_DOWNGRADE" =>
            "A newer version is already installed.",
        "INSTALL_FAILED_INSUFFICIENT_STORAGE" =>
            "Not enough free space on the headset.",
        "INSTALL_FAILED_NO_MATCHING_ABIS" =>
            "This APK is built for a different CPU — it will not run on Quest.",
        "INSTALL_FAILED_OLDER_SDK" =>
            "This APK needs a newer Android version than the headset has.",
        "INSTALL_PARSE_FAILED_NO_CERTIFICATES" or "INSTALL_PARSE_FAILED_UNEXPECTED_EXCEPTION" =>
            "The APK is not signed or is damaged.",
        "INSTALL_FAILED_INVALID_APK" or "INSTALL_PARSE_FAILED_NOT_APK" =>
            "The file is not a valid APK.",
        "INSTALL_FAILED_DUPLICATE_PERMISSION" =>
            "Another installed app already defines the same permission.",
        _ => null,
    };

    private static string FirstUseful(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("Failure", StringComparison.OrdinalIgnoreCase) ||
                                 l.Contains("Error", StringComparison.OrdinalIgnoreCase));
        line ??= output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown error";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
