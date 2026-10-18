"""Builds Quest Apps for Windows, macOS and Linux and packages each one.

    python tools/build-release.py            # all targets
    python tools/build-release.py linux-x64  # selected targets

Output: bin/Release/<version>/
    QuestApps.exe + adb/ + README.md            Windows x64 (folder, as before; Windows-only guide)
    QuestApps-<v>-macos-arm64.zip               macOS Apple Silicon (QuestApps.app)
    QuestApps-<v>-macos-x64.zip                 macOS Intel (QuestApps.app)
    QuestApps-<v>-linux-x64.tar.gz              Linux x64
    QuestApps-<v>-linux-arm64.tar.gz            Linux ARM64

Archives are written with Unix permissions, so the executables keep their +x bit
even though they are built on Windows.
"""
import os
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "QuestApps.csproj"
MULTI_README = ROOT / "README-multiplatform.md"  # guide for macOS/Linux packages
TARGETS = ["win-x64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64"]

UDEV_RULE = (
    "# Meta Quest (vendor 2833): let adb reach the headset over USB without root.\n"
    "# sudo cp 99-meta-quest.rules /etc/udev/rules.d/ && sudo udevadm control --reload-rules\n"
    'SUBSYSTEM=="usb", ATTR{idVendor}=="2833", MODE="0666", GROUP="plugdev"\n'
)


def version() -> str:
    m = re.search(r"<Version>([\d.]+)</Version>", PROJECT.read_text(encoding="utf-8"))
    full = m.group(1) if m else "0.0.0"
    parts = full.split(".")
    return ".".join(parts[:2]) if parts[2:] == ["0"] else full


def publish(rid: str, out: Path) -> None:
    if out.exists():
        shutil.rmtree(out)
    cmd = [
        "dotnet", "publish", str(PROJECT), "-nologo", "-v", "q", "-c", "Release",
        "-r", rid, "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none", "-o", str(out),
    ]
    print(f"[{rid}] dotnet publish …", flush=True)
    subprocess.run(cmd, check=True)


def has_code_signature(macho: Path) -> bool:
    """True when the (thin, 64-bit) Mach-O binary has an LC_CODE_SIGNATURE load command.
    Apple Silicon refuses to run arm64 code without at least an ad-hoc signature."""
    data = macho.read_bytes()
    if struct.unpack_from("<I", data, 0)[0] != 0xFEEDFACF:
        return False
    ncmds = struct.unpack_from("<I", data, 16)[0]
    off = 32
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, off)
        if cmd == 0x1D:
            return True
        off += size
    return False


def icns_from_png(png: Path) -> bytes:
    """Minimal .icns with one 256x256 PNG entry (type ic08)."""
    body = png.read_bytes()
    entry = b"ic08" + struct.pack(">I", len(body) + 8) + body
    return b"icns" + struct.pack(">I", len(entry) + 8) + entry


def info_plist(ver: str) -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Quest Apps</string>
    <key>CFBundleDisplayName</key><string>Quest Apps</string>
    <key>CFBundleIdentifier</key><string>app.questapps.desktop</string>
    <key>CFBundleExecutable</key><string>QuestApps</string>
    <key>CFBundleIconFile</key><string>QuestApps.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>{ver}</string>
    <key>CFBundleVersion</key><string>{ver}</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"""


def is_executable(rel: str) -> bool:
    name = rel.rsplit("/", 1)[-1]
    return name in ("QuestApps", "adb", "fastboot", "etc1tool", "hprof-conv", "make_f2fs",
                    "make_f2fs_casefold", "mke2fs", "sqlite3", "dmtracedump") or name.endswith(".sh")


def add_tree_zip(zf: zipfile.ZipFile, src: Path, prefix: str) -> None:
    for path in sorted(src.rglob("*")):
        rel = path.relative_to(src).as_posix()
        name = f"{prefix}/{rel}" if prefix else rel
        if path.is_dir():
            info = zipfile.ZipInfo(name + "/")
            info.create_system = 3
            info.external_attr = (0o40755 << 16) | 0x10
            zf.writestr(info, b"")
            continue
        info = zipfile.ZipInfo(name)
        info.create_system = 3
        mode = 0o100755 if is_executable(rel) else 0o100644
        info.external_attr = mode << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        zf.writestr(info, path.read_bytes())


def add_tree_tar(tf: tarfile.TarFile, src: Path, prefix: str) -> None:
    for path in sorted(src.rglob("*")):
        rel = path.relative_to(src).as_posix()
        info = tf.gettarinfo(str(path), arcname=f"{prefix}/{rel}")
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        if path.is_dir():
            info.mode = 0o755
            tf.addfile(info)
        else:
            info.mode = 0o755 if is_executable(rel) else 0o644
            with open(path, "rb") as f:
                tf.addfile(info, f)


def package_macos(rid: str, staged: Path, out_dir: Path, ver: str) -> Path:
    arch = rid.split("-")[1]
    work = staged.parent / f"{rid}-bundle"
    if work.exists():
        shutil.rmtree(work)
    contents = work / "QuestApps.app" / "Contents"
    (contents / "MacOS").mkdir(parents=True)
    (contents / "Resources").mkdir(parents=True)
    shutil.copy2(staged / "QuestApps", contents / "MacOS" / "QuestApps")
    if (staged / "adb").exists():
        shutil.copytree(staged / "adb", contents / "MacOS" / "adb")
    (contents / "Info.plist").write_text(info_plist(ver), encoding="utf-8", newline="\n")
    (contents / "Resources" / "QuestApps.icns").write_bytes(icns_from_png(ROOT / "icon256.png"))
    shutil.copy2(MULTI_README, work / MULTI_README.name)

    if not has_code_signature(contents / "MacOS" / "QuestApps"):
        print(f"[{rid}] WARNING: executable has no code signature — on Apple Silicon run "
              "'codesign --force --deep -s - QuestApps.app' on the Mac.")

    archive = out_dir / f"QuestApps-{ver}-macos-{arch}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
        add_tree_zip(zf, work, "")
    return archive


def package_linux(rid: str, staged: Path, out_dir: Path, ver: str) -> Path:
    arch = rid.split("-")[1]
    top = f"QuestApps-{ver}-linux-{arch}"
    (staged / "99-meta-quest.rules").write_text(UDEV_RULE, encoding="utf-8", newline="\n")
    shutil.copy2(ROOT / "icon256.png", staged / "questapps.png")
    (staged / "questapps.desktop").write_text(
        "[Desktop Entry]\nType=Application\nName=Quest Apps\n"
        "Comment=Manage apps on Meta Quest headsets\n"
        "Exec=QuestApps\nIcon=questapps\nCategories=Utility;\nTerminal=false\n",
        encoding="utf-8", newline="\n")
    shutil.copy2(MULTI_README, staged / MULTI_README.name)
    archive = out_dir / f"{top}.tar.gz"
    with tarfile.open(archive, "w:gz") as tf:
        add_tree_tar(tf, staged, top)
    return archive


def main() -> None:
    targets = sys.argv[1:] or TARGETS
    ver = version()
    out_dir = ROOT / "bin" / "Release" / ver
    out_dir.mkdir(parents=True, exist_ok=True)
    staging = ROOT / "obj" / "release-staging"

    for rid in targets:
        if rid.startswith("win"):
            publish(rid, out_dir)           # Windows: plain folder, like earlier releases
            print(f"[{rid}] -> {out_dir}")
            continue
        staged = staging / rid
        publish(rid, staged)
        if not (staged / "adb").exists():
            print(f"[{rid}] note: no bundled adb (adb-{'macos' if rid.startswith('osx') else 'linux'}/ is empty) "
                  "— the app will use an adb installed on the system.")
        archive = package_macos(rid, staged, out_dir, ver) if rid.startswith("osx") \
            else package_linux(rid, staged, out_dir, ver)
        print(f"[{rid}] -> {archive.name} ({archive.stat().st_size / 1048576:.1f} MB)")


if __name__ == "__main__":
    main()
