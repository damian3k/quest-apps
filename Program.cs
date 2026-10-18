using System;
using System.Linq;
using Avalonia;

namespace QuestApps;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Debug helpers:
        //   QuestApps --label-file <path-to.apk>   parse label from a local APK
        //   QuestApps --label <package>            parse label from an APK on the connected headset
        //   QuestApps --scan <path> [<path>…]      what the Install tab would queue for dropped paths
        if (args.Length >= 2 && args[0] == "--scan")
        {
            var (items, skipped) = InstallScanner.Collect(args.Skip(1));
            foreach (var item in items)
            {
                Console.WriteLine($"{item.DisplayName} {item.VersionText} — {item.DetailsText}");
                foreach (var obb in item.ObbFiles)
                    Console.WriteLine($"    obb: {obb}");
            }
            foreach (var s in skipped)
                Console.WriteLine("skipped: " + s);
            return;
        }
        if (args.Length >= 2 && args[0] == "--label-file")
        {
            var info = ApkLabelReader.ReadInfo(new LocalApkSource(args[1]), Console.WriteLine);
            Console.WriteLine($"RESULT: label=\"{info?.Label ?? "(null)"}\" version=\"{info?.VersionName ?? "(null)"}\" icon={info?.IconPng?.Length ?? 0}B");
            return;
        }
        if (args.Length >= 2 && args[0] == "--label")
        {
            var adb = new AdbService();
            if (!adb.TrySetAdb(AdbService.BundledAdb))
            {
                Console.WriteLine("adb not ready: " + adb.LastError);
                return;
            }
            var listing = adb.Run("shell pm list packages -3 -f").Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.EndsWith("=" + args[1], StringComparison.Ordinal));
            if (listing == null)
            {
                Console.WriteLine("package not found on device: " + args[1]);
                return;
            }
            var apk = listing["package:".Length..listing.LastIndexOf('=')];
            var info = ApkLabelReader.ReadInfo(new DeviceApkSource(adb, apk), Console.WriteLine);
            Console.WriteLine($"RESULT: label=\"{info?.Label ?? "(null)"}\" version=\"{info?.VersionName ?? "(null)"}\" icon={info?.IconPng?.Length ?? 0}B");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
