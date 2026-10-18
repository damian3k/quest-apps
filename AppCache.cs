using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QuestApps;

// Local cache of labels/versions/icons so the second (and every later) scan is instant.
internal static class AppCache
{
    private static readonly string Dir = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "QuestApps", "cache");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private sealed class Meta
    {
        public string? Label { get; set; }
        public string? Version { get; set; }
    }

    public static ApkInfo? Load(string package)
    {
        try
        {
            var metaPath = Path.Combine(Dir, package + ".json");
            if (!File.Exists(metaPath))
                return null;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath));
            if (meta == null)
                return null;
            byte[]? icon = null;
            var iconPath = Path.Combine(Dir, package + ".icon");
            if (File.Exists(iconPath))
                icon = File.ReadAllBytes(iconPath);
            return new ApkInfo(meta.Label, meta.Version, icon);
        }
        catch { return null; }
    }

    public static void Save(string package, ApkInfo info)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, package + ".json"),
                JsonSerializer.Serialize(new Meta { Label = info.Label, Version = info.VersionName }, Json));
            if (info.IconPng is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(Dir, package + ".icon"), info.IconPng);
        }
        catch { /* cache is best-effort */ }
    }
}

// Headsets seen before (id = ro.serialno, model, last Wi-Fi address), so the app can
// offer and reconnect them without a cable, plus the last selected one.
internal sealed class KnownHeadset
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Name { get; set; }   // user-given, e.g. "Station 3"
    public string? Ip { get; set; }
    public System.DateTime LastSeen { get; set; }
}

internal static class HeadsetMemory
{
    private static readonly string Dir = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "QuestApps");
    private static readonly string FilePath = Path.Combine(Dir, "headsets.json");
    private static readonly string SelectedPath = Path.Combine(Dir, "selected-headset.txt");
    private static readonly object Gate = new();

    public static List<KnownHeadset> Load()
    {
        lock (Gate)
        {
            try
            {
                var all = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<KnownHeadset>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
                // "ip:port" entries were stored by 4.0 betas when the serial was unreadable
                all.RemoveAll(h => h.Id.Contains(':'));
                return all;
            }
            catch { return new(); }
        }
    }

    public static void Remember(string id, string model, string? ip)
    {
        lock (Gate)
        {
            var all = Load();
            var h = all.FirstOrDefault(x => x.Id == id);
            // called on every background poll — only touch the disk when something changed
            if (h != null && h.Model == model && (ip == null || h.Ip == ip) &&
                System.DateTime.Now - h.LastSeen < System.TimeSpan.FromHours(1))
                return;
            if (h == null)
                all.Add(h = new KnownHeadset { Id = id });
            h.Model = model;
            if (ip != null)
                h.Ip = ip;
            h.LastSeen = System.DateTime.Now;
            // an address belongs to one headset only (DHCP may hand it to another one)
            if (ip != null)
                foreach (var other in all.Where(x => x != h && x.Ip == ip))
                    other.Ip = null;
            Save(all);
        }
    }

    public static void SetName(string id, string model, string? name)
    {
        lock (Gate)
        {
            var all = Load();
            var h = all.FirstOrDefault(x => x.Id == id);
            if (h == null)
                all.Add(h = new KnownHeadset { Id = id, Model = model, LastSeen = System.DateTime.Now });
            h.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            Save(all);
        }
    }

    private static void Save(List<KnownHeadset> all)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
        }
        catch { /* best-effort */ }
    }

    public static string? Selected
    {
        get
        {
            try { return File.Exists(SelectedPath) ? File.ReadAllText(SelectedPath).Trim() : null; }
            catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(SelectedPath, value ?? "");
            }
            catch { /* best-effort */ }
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}
