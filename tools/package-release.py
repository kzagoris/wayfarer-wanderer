#!/usr/bin/env python3
"""Publish, inventory and package one Wayfarer Wanderer release target."""
import argparse
import json
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


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
        'mkdir -p "$HOME/.local/bin"\n'
        'ln -sf "$prefix/WayfarerWanderer.App" "$HOME/.local/bin/wayfarer-wanderer"\n',
        encoding="utf-8",
    )
    installer.chmod(0o755)
    with tarfile.open(output, "w:gz") as archive:
        for item in staging.iterdir():
            archive.add(item, arcname=item.name)


def windows(publish, output, rid):
    vpk = shutil.which("vpk")
    prefix = [vpk] if vpk is not None else ["dotnet", "tool", "run", "vpk"]
    if vpk is None:
        command(["dotnet", "tool", "restore"])
    command([*prefix, "pack", "--packId", "WayfarerWanderer", "--packVersion", "1.0.0",
             "--packDir", publish, "--mainExe", "WayfarerWanderer.App.exe",
             "--outputDir", output.parent, "--runtime", rid])


def macos(publish, output):
    hdiutil = shutil.which("hdiutil")
    if hdiutil is None:
        raise SystemExit("hdiutil is required for the macOS DMG")
    bundle = output.parent / "WayfarerWanderer.app"
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
        '<string>Wayfarer Wanderer</string></dict></plist>\n',
        encoding="utf-8",
    )
    command([hdiutil, "create", "-volname", "Wayfarer Wanderer", "-srcfolder", bundle, "-ov", "-format", "UDZO", output])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=(
        "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"
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
    elif arguments.rid.startswith("win-"):
        windows(publish, output, arguments.rid)
    else:
        macos(publish, output)
    (output.parent / f"{output.stem}-package.json").write_text(
        json.dumps({"rid": arguments.rid, "package": str(output), "inventory": str(inventory)}, indent=2) + "\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
