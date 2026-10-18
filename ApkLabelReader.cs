using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace QuestApps;

// Reads android:label, android:versionName and the launcher icon from an APK
// WITHOUT reading the whole file: only the ZIP central directory,
// AndroidManifest.xml, resources.arsc and the icon entry are fetched.
internal interface IApkSource
{
    long Length { get; }
    byte[] Read(long start, int len);
}

internal sealed class LocalApkSource : IApkSource
{
    private readonly string _path;
    public LocalApkSource(string path) => _path = path;
    public long Length => new FileInfo(_path).Length;

    public byte[] Read(long start, int len)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = fs.Read(buffer, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < len)
            throw new IOException($"short read ({read}/{len})");
        return buffer;
    }
}

internal sealed class DeviceApkSource : IApkSource
{
    private readonly AdbService _adb;
    private readonly string _path;
    private readonly long _length;

    public DeviceApkSource(AdbService adb, string path)
    {
        _adb = adb;
        _path = path;
        var s = adb.Run($"shell stat -c %s {path}").Trim();
        if (!long.TryParse(s, out _length) || _length < 100)
            throw new IOException($"stat failed for {path}: {s}");
    }

    public long Length => _length;

    public byte[] Read(long start, int len)
    {
        const int bs = 4096;
        long firstBlock = start / bs;
        int head = (int)(start % bs);
        int blocks = (head + len + bs - 1) / bs;
        byte[] raw = _adb.RunBinary($"exec-out dd if={_path} bs={bs} skip={firstBlock} count={blocks}");
        if (raw.Length < head + len)
            throw new IOException($"short read from {_path} ({raw.Length} B)");
        var result = new byte[len];
        Array.Copy(raw, head, result, 0, len);
        return result;
    }
}

internal sealed record ApkInfo(string? Label, string? VersionName, byte[]? IconPng, string? Package = null);

internal static class ApkLabelReader
{
    public static ApkInfo? ReadInfo(IApkSource source, Action<string>? log = null)
    {
        long fileSize = source.Length;
        log?.Invoke($"file size: {fileSize}");

        int tailLen = (int)Math.Min(65536, fileSize);
        byte[] tail = source.Read(fileSize - tailLen, tailLen);
        int eocd = LastIndexOf(tail, 0x50, 0x4B, 0x05, 0x06);
        if (eocd < 0)
        {
            log?.Invoke("EOCD not found");
            return null;
        }

        long cdSize = LE32(tail, eocd + 12);
        long cdOff = LE32(tail, eocd + 16);
        log?.Invoke($"central dir: off={cdOff} size={cdSize}");
        byte[] cd = cdOff >= fileSize - tailLen
            ? Slice(tail, (int)(cdOff - (fileSize - tailLen)), (int)cdSize)
            : source.Read(cdOff, (int)cdSize);

        var manifestEntry = FindEntry(cd, "AndroidManifest.xml");
        if (manifestEntry == null)
        {
            log?.Invoke("AndroidManifest.xml not found in central dir");
            return null;
        }

        byte[] manifest = ReadEntry(source, manifestEntry);
        log?.Invoke($"manifest decoded: {manifest.Length} B");
        var mi = Axml.GetAppInfo(manifest);
        log?.Invoke($"manifest: labelLit=\"{mi.LabelLiteral}\" labelRes=0x{mi.LabelRes:X8} verLit=\"{mi.VersionLiteral}\" verRes=0x{mi.VersionRes:X8} iconRes=0x{mi.IconRes:X8}");

        // anything to resolve through resources.arsc?
        bool needArsc = (mi.LabelLiteral == null && mi.LabelRes != 0)
                     || (mi.VersionLiteral == null && mi.VersionRes != 0)
                     || mi.IconRes != 0;
        string? label = mi.LabelLiteral;
        string? version = mi.VersionLiteral;
        string? iconPath = null;

        if (needArsc)
        {
            var arscEntry = FindEntry(cd, "resources.arsc");
            if (arscEntry != null)
            {
                byte[] arsc = ReadEntry(source, arscEntry);
                log?.Invoke($"arsc decoded: {arsc.Length} B");
                if (mi.LabelLiteral == null && mi.LabelRes != 0)
                    label = Arsc.ResolveString(arsc, mi.LabelRes);
                if (mi.VersionLiteral == null && mi.VersionRes != 0)
                    version ??= Arsc.ResolveString(arsc, mi.VersionRes);
                if (mi.IconRes != 0)
                    iconPath = Arsc.ResolveString(arsc, mi.IconRes);
                log?.Invoke($"arsc: label=\"{label}\" version=\"{version}\" iconPath=\"{iconPath}\"");
            }
        }

        byte[]? iconPng = null;
        if (!string.IsNullOrEmpty(iconPath) && iconPath.StartsWith("res/", StringComparison.Ordinal)
            && !iconPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            var iconEntry = FindEntry(cd, iconPath);
            if (iconEntry != null)
            {
                try { iconPng = ReadEntry(source, iconEntry); }
                catch { iconPng = null; }
                log?.Invoke($"icon read: {iconPng?.Length ?? 0} B");
            }
        }

        return new ApkInfo(label, version, iconPng, mi.Package);
    }

    private sealed class Entry
    {
        public long LocalOff;
        public int CompSize;
        public int Method;
    }

    private static Entry? FindEntry(byte[] cd, string name)
    {
        int pos = 0;
        while (pos + 46 <= cd.Length)
        {
            if (LE32(cd, pos) != 0x02014B50)
                break;
            int method = LE16(cd, pos + 10);
            int compSize = (int)LE32(cd, pos + 20);
            int nameLen = LE16(cd, pos + 28);
            int extraLen = LE16(cd, pos + 30);
            int commentLen = LE16(cd, pos + 32);
            long localOff = LE32(cd, pos + 42);
            if (pos + 46 + nameLen > cd.Length)
                break;
            string entryName = Encoding.UTF8.GetString(cd, pos + 46, nameLen);
            if (entryName == name)
                return new Entry { LocalOff = localOff, CompSize = compSize, Method = method };
            pos += 46 + nameLen + extraLen + commentLen;
        }
        return null;
    }

    private static byte[] ReadEntry(IApkSource source, Entry e)
    {
        if (e.Method is not (0 or 8))
            return Array.Empty<byte>();

        byte[] head = source.Read(e.LocalOff, 30);
        int nameLen = LE16(head, 26);
        int extraLen = LE16(head, 28);
        byte[] data = source.Read(e.LocalOff + 30 + nameLen + extraLen, e.CompSize);

        if (e.Method == 0)
            return data;

        using var src = new MemoryStream(data);
        using var inflate = new DeflateStream(src, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        inflate.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static byte[] Slice(byte[] src, int offset, int len)
    {
        var result = new byte[len];
        Array.Copy(src, offset, result, 0, len);
        return result;
    }

    private static int LastIndexOf(byte[] b, params byte[] pattern)
    {
        for (int i = b.Length - pattern.Length; i >= 0; i--)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
                if (b[i + j] != pattern[j]) { ok = false; break; }
            if (ok)
                return i;
        }
        return -1;
    }

    internal static ushort LE16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    internal static uint LE32(byte[] b, int i) =>
        (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16)) | ((uint)b[i + 3] << 24);

    // ---- Android binary XML (AXML) ----
    private static class Axml
    {
        public sealed class ManifestInfo
        {
            public string? Package;
            public string? LabelLiteral;
            public uint LabelRes;
            public string? VersionLiteral;
            public uint VersionRes;
            public uint IconRes;
        }

        public static ManifestInfo GetAppInfo(byte[] xml)
        {
            var result = new ManifestInfo();
            if (xml.Length < 8 || LE16(xml, 0) != 0x0003)
                return result;

            var pool = Array.Empty<string>();
            int pos = 8;
            while (pos + 8 <= xml.Length)
            {
                ushort type = LE16(xml, pos);
                uint size = LE32(xml, pos + 4);
                if (size == 0)
                    break;

                if (type == 0x0001)
                {
                    pool = ReadPool(xml, pos);
                }
                else if (type == 0x0102) // start element
                {
                    uint nameIdx = LE32(xml, pos + 20);
                    string element = nameIdx < pool.Length ? pool[nameIdx] : "";
                    // attributeStart/attributeSize liczone są od początku attrExt (chunk+16)
                    int attrStart = LE16(xml, pos + 24);
                    int attrSize = Math.Max((int)LE16(xml, pos + 26), 20);
                    int attrCount = LE16(xml, pos + 28);

                    bool isManifest = element == "manifest";
                    bool isApplication = element == "application";
                    for (int a = 0; a < attrCount && (isManifest || isApplication); a++)
                    {
                        int at = pos + 16 + attrStart + a * attrSize;
                        if (at + 20 > xml.Length)
                            break;
                        uint attrNameIdx = LE32(xml, at + 4);
                        string attrName = attrNameIdx < pool.Length ? pool[attrNameIdx] : "";
                        uint rawValue = LE32(xml, at + 8);
                        byte valueType = xml[at + 15];
                        uint data = LE32(xml, at + 16);

                        string? literal = rawValue != 0xFFFFFFFF && rawValue < pool.Length
                            ? pool[rawValue] : null;
                        uint resId = valueType == 0x01 ? data : 0;

                        if (isManifest && attrName == "package")
                        {
                            result.Package = literal;
                        }
                        else if (isManifest && attrName == "versionName")
                        {
                            result.VersionLiteral = literal;
                            result.VersionRes = resId;
                        }
                        else if (isApplication && attrName == "label")
                        {
                            result.LabelLiteral = literal;
                            result.LabelRes = resId;
                        }
                        else if (isApplication && attrName == "icon")
                        {
                            result.IconRes = resId;
                        }
                    }
                }
                pos += (int)size;
            }
            return result;
        }

        internal static string[] ReadPool(byte[] b, int chunkStart)
        {
            if (chunkStart + 28 > b.Length)
                return Array.Empty<string>();
            int count = (int)LE32(b, chunkStart + 8);
            uint flags = LE32(b, chunkStart + 16);
            int stringsStart = (int)LE32(b, chunkStart + 20);
            bool utf8 = (flags & 0x100) != 0;
            if (count < 0 || count > 200000)
                return Array.Empty<string>();

            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                int offPos = chunkStart + 28 + 4 * i;
                if (offPos + 4 > b.Length)
                    break;
                int off = (int)LE32(b, offPos);
                int p = chunkStart + stringsStart + off;
                if (p < 0 || p >= b.Length)
                    continue;
                try
                {
                    if (utf8)
                    {
                        _ = ReadLen8(b, ref p);
                        int byteLen = ReadLen8(b, ref p);
                        result[i] = Encoding.UTF8.GetString(b, p, Math.Min(byteLen, b.Length - p));
                    }
                    else
                    {
                        int n = ReadLen16(b, ref p);
                        if (n > (b.Length - p) / 2)
                            n = (b.Length - p) / 2;
                        result[i] = Encoding.Unicode.GetString(b, p, n * 2);
                    }
                }
                catch { result[i] = ""; }
            }
            return result;
        }

        private static int ReadLen8(byte[] b, ref int p)
        {
            int v = b[p++];
            if ((v & 0x80) != 0)
                v = ((v & 0x7F) << 8) | b[p++];
            return v;
        }

        private static int ReadLen16(byte[] b, ref int p)
        {
            int v = LE16(b, p);
            p += 2;
            if ((v & 0x8000) != 0)
            {
                int hi = v & 0x7FFF;
                v = (hi << 16) | LE16(b, p);
                p += 2;
            }
            return v;
        }
    }

    // ---- resources.arsc ----
    private static class Arsc
    {
        public static string? ResolveString(byte[] table, uint resId)
        {
            byte pkgWanted = (byte)((resId >> 24) & 0xFF);
            byte typeWanted = (byte)((resId >> 16) & 0xFF);
            int entryWanted = (int)(resId & 0xFFFF);

            if (table.Length < 16 || LE16(table, 0) != 0x0002)
                return null;

            int pos = 12; // table header (8) + packageCount (4)
            if (pos + 8 > table.Length || LE16(table, pos) != 0x0001)
                return null;
            string[] global = Axml.ReadPool(table, pos);
            pos += (int)LE32(table, pos + 4);

            string? fallback = null;
            while (pos + 8 <= table.Length)
            {
                ushort chunkType = LE16(table, pos);
                uint chunkSize = LE32(table, pos + 4);
                if (chunkSize == 0)
                    break;

                if (chunkType == 0x0200) // ResTable_package
                {
                    byte pkgId = table[pos + 8];
                    if (pkgId == pkgWanted)
                    {
                        int p = pos + LE16(table, pos + 2);
                        int limit = pos + (int)chunkSize;
                        while (p + 8 <= limit)
                        {
                            ushort t = LE16(table, p);
                            uint s = LE32(table, p + 4);
                            if (s == 0)
                                break;
                            if (t == 0x0201 && table[p + 8] == typeWanted) // ResTable_type
                            {
                                var value = ReadEntryValue(table, p, entryWanted, global);
                                if (value != null)
                                {
                                    if (IsDefaultConfig(table, p))
                                        return value;
                                    fallback ??= value;
                                }
                            }
                            p += (int)s;
                        }
                    }
                }
                pos += (int)chunkSize;
            }
            return fallback;
        }

        private static string? ReadEntryValue(byte[] t, int chunk, int entry, string[] global)
        {
            int hdrSize = LE16(t, chunk + 2);
            int entryCount = (int)LE32(t, chunk + 12);
            int entriesStart = (int)LE32(t, chunk + 16);
            if (entry >= entryCount)
                return null;

            int offPos = chunk + hdrSize + 4 * entry;
            if (offPos + 4 > t.Length)
                return null;
            uint rel = LE32(t, offPos);
            if (rel == 0xFFFFFFFF)
                return null;

            int e = chunk + entriesStart + (int)rel;
            if (e + 16 > t.Length)
                return null;
            int flags = LE16(t, e + 2);
            if ((flags & 1) != 0)
                return null; // complex entry (string array etc.)

            byte valueType = t[e + 11];
            uint data = LE32(t, e + 12);
            return valueType == 0x03 && data < (uint)global.Length
                ? global[data]
                : null;
        }

        private static bool IsDefaultConfig(byte[] t, int chunk)
            => chunk + 30 <= t.Length && t[chunk + 28] == 0 && t[chunk + 29] == 0;
    }
}
