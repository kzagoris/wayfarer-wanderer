#!/usr/bin/env python3
"""Publish, inventory and package one Wayfarer Wanderer release target."""
import argparse
import json
import re
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

# The Linux desktop entry's id, as src/WayfarerWanderer.App/Shell/Welcome/LinuxRecentlyUsed.cs names it.
DESKTOP_ENTRY = "io.github.kzagoris.WayfarerWanderer"


def version():
    """Wayfarer's own version, as Directory.Build.props declares it."""
    found = re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8"))
    if found is None:
        raise SystemExit("Directory.Build.props declares no <Version>")
    return found.group(1).strip()


def portable_of(output, rid):
    """The portable build beside a package, named as Updates/Releases.cs's ThisSystemsAsset(portable: true) names it:
    Windows' WayfarerWanderer-windows-x64-portable.zip, macOS's WayfarerWanderer-macos-arm64.zip. Linux's archive is
    both."""
    return output.with_name(f"{output.stem}-portable.zip") if rid.startswith("win") else output.with_suffix(".zip")


def command(arguments, *, cwd=ROOT):
    subprocess.run([str(argument) for argument in arguments], cwd=cwd, check=True)


def linux(publish, output):
    staging = output.parent / "linux-package"
    if staging.exists():
        shutil.rmtree(staging)
    (staging / "WayfarerWanderer").mkdir(parents=True)
    shutil.copytree(publish, staging / "WayfarerWanderer", dirs_exist_ok=True)
    installer = staging / "install.sh"
    installer.write_text(
        "#!/usr/bin/env sh\n"
        "set -eu\n"
        'prefix="${1:-"$HOME/.local/share/wayfarer-wanderer"}"\n'
        'mkdir -p "$prefix"\n'
        'cp -R WayfarerWanderer/. "$prefix/"\n'
        'sh "$prefix/refresh-desktop.sh" "$prefix"\n',
        encoding="utf-8",
    )
    (staging / "WayfarerWanderer" / "refresh-desktop.sh").write_text(
        "#!/usr/bin/env sh\n"
        "set -eu\n"
        'prefix="$1"\n'
        'mkdir -p "$HOME/.local/bin"\n'
        'ln -sf "$prefix/WayfarerWanderer.App" "$HOME/.local/bin/wayfarer-wanderer"\n'
        # The desktop entry the app finds itself by to list its recent folders as recently used (LinuxRecentlyUsed.cs),
        # its Exec the installed program itself, which KDE matches the list's entries to.
        'applications="${XDG_DATA_HOME:-"$HOME/.local/share"}/applications"\n'
        'mkdir -p "$applications"\n'
        f'cat > "$applications/{DESKTOP_ENTRY}.desktop" <<EOF\n'
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=Wayfarer Wanderer\n"
        "Comment=An IDE for Java and Kotlin\n"
        'Exec="$prefix/WayfarerWanderer.App" %f\n'
        "Terminal=false\n"
        "Categories=Development;IDE;\n"
        f"StartupWMClass=WayfarerWanderer.App\n"
        "EOF\n"
        'command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$applications" || true\n',
        encoding="utf-8",
    )
    installer.chmod(0o755)
    with tarfile.open(output, "w:gz") as archive:
        for item in staging.iterdir():
            archive.add(item, arcname=item.name)


def windows(publish, output, rid):
    command(["dotnet", "tool", "restore"])
    command(["dotnet", "tool", "run", "vpk", "pack", "--packId", "WayfarerWanderer", "--packVersion", version(),
             "--packDir", publish, "--mainExe", "WayfarerWanderer.App.exe",
             "--outputDir", output.parent, "--runtime", rid, "--channel", rid, "--noPortable"])
    setup, = output.parent.glob("*-Setup.exe")
    setup.replace(output)
    # The portable build (ThisCopy.cs): the app's folder as published, nothing of Velopack's, in a WayfarerWanderer folder.
    # A data folder made beside its executable keeps all its state there.
    staging = output.parent / "windows-portable"
    if staging.exists():
        shutil.rmtree(staging)
    shutil.copytree(publish, staging / "WayfarerWanderer")
    # Never taken for an installed copy, whatever the publish folder holds.
    (staging / "WayfarerWanderer" / "sq.version").unlink(missing_ok=True)
    archive = shutil.make_archive(str(portable_of(output, rid).with_suffix("")), "zip", root_dir=staging, base_dir="WayfarerWanderer")
    return Path(archive)


def macos(publish, output):
    hdiutil = shutil.which("hdiutil")
    if hdiutil is None:
        raise SystemExit("hdiutil is required for the macOS DMG")
    staging = output.parent / "mac-package"
    if staging.exists():
        shutil.rmtree(staging)
    bundle = staging / "WayfarerWanderer.app"
    contents = bundle / "Contents"
    macos_dir = contents / "MacOS"
    resources = contents / "Resources"
    macos_dir.mkdir(parents=True, exist_ok=True)
    resources.mkdir(parents=True, exist_ok=True)
    shutil.copytree(publish, resources / "app", dirs_exist_ok=True)
    launcher = macos_dir / "WayfarerWanderer"
    launcher.write_text(
        "#!/bin/sh\n"
        'exec "$(dirname "$0")/../Resources/app/WayfarerWanderer.App" "$@"\n',
        encoding="utf-8",
    )
    launcher.chmod(0o755)
    (contents / "Info.plist").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" '
        '"http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
        '<plist version="1.0"><dict><key>CFBundleExecutable</key>'
        '<string>WayfarerWanderer</string><key>CFBundleIdentifier</key>'
        '<string>org.wayfarer.WayfarerWanderer</string><key>CFBundleName</key>'
        f'<string>Wayfarer Wanderer</string><key>CFBundleShortVersionString</key><string>{version()}</string><key>CFBundleVersion</key><string>{version()}</string></dict></plist>\n',
        encoding="utf-8",
    )
    command(["codesign", "--force", "--deep", "--sign", "-", bundle])
    command([hdiutil, "create", "-volname", "Wayfarer Wanderer", "-srcfolder", staging, "-ov", "-format", "UDZO", output])
    # The portable build (ThisCopy.cs): the signed app in a WayfarerWanderer folder, a data folder made beside the app
    # keeping all its state there.
    portable_staging = output.parent / "mac-portable"
    if portable_staging.exists():
        shutil.rmtree(portable_staging)
    (portable_staging / "WayfarerWanderer").mkdir(parents=True)
    command(["ditto", bundle, portable_staging / "WayfarerWanderer" / bundle.name])
    archive = portable_of(output, "osx")
    archive.unlink(missing_ok=True)
    command(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", portable_staging / "WayfarerWanderer", archive])
    return archive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=(
        "win-x64", "osx-arm64", "linux-x64"
    ))
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()

    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    publish = ROOT / "artifacts" / "publish" / "WayfarerWanderer.App" / f"release_{arguments.rid}"
    command([
        "dotnet", "restore", "src/WayfarerWanderer.App",
        f"-p:RuntimeIdentifier={arguments.rid}", "-p:SelfContained=true", "-p:Configuration=Release",
        "--locked-mode", "--configfile", "vendor/passenger/nuget.config",
    ])
    command([
        "dotnet", "publish", "src/WayfarerWanderer.App", "-c", "Release", "-r", arguments.rid,
        "--self-contained", "--no-restore",
    ])
    inventory = output.parent / f"{output.stem}-inventory.json"
    command([sys.executable, "tools/check-publish.py", publish, inventory])
    if arguments.rid.startswith("linux-"):
        linux(publish, output)
        portable = output
    elif arguments.rid.startswith("win-"):
        portable = windows(publish, output, arguments.rid)
    else:
        portable = macos(publish, output)
    (output.parent / f"{output.stem}-package.json").write_text(
        json.dumps({"rid": arguments.rid, "package": str(output), "portable": str(portable), "inventory": str(inventory)}, indent=2) + "\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
