#!/usr/bin/env python3
"""Builds Wayfarer Wanderer's JVM project host and stages it under artifacts/jvm-host.

The host is built by its own checked-in Gradle wrapper (jvm/project-host), whose distribution checksum, dependency
locks and verification metadata are pinned. Building it needs a JDK 21 or later: JAVA_HOME when it names one, else the
java on PATH. A clean clone also needs network access once, for the Gradle distribution and the host's dependencies.

    python tools/build-jvm-host.py [--stage DIR] [--test]
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOST = ROOT / "jvm" / "project-host"
MINIMUM_JAVA = 21


def java_home() -> Path:
    """The JDK to build with: JAVA_HOME, else the one whose java is on PATH."""
    candidates: list[Path] = []
    if os.environ.get("JAVA_HOME"):
        candidates.append(Path(os.environ["JAVA_HOME"]))
    on_path = shutil.which("java")
    if on_path:
        candidates.append(Path(on_path).resolve().parent.parent)
    for home in candidates:
        feature = release_feature(home)
        if feature is not None and feature >= MINIMUM_JAVA and has_javac(home):
            return home
    named = ", ".join(f"{home} (Java {release_feature(home) or '?'})" for home in candidates) or "none"
    sys.exit(f"build-jvm-host: the JVM project host needs a JDK {MINIMUM_JAVA} or later to build; found {named}. "
             "Set JAVA_HOME to one, or build with -p:WayfarerSkipJvmHost=true.")


def release_feature(home: Path) -> int | None:
    try:
        text = (home / "release").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    match = re.search(r'^JAVA_VERSION="(?:1\.)?(\d+)', text, re.MULTILINE)
    return int(match.group(1)) if match else None


def has_javac(home: Path) -> bool:
    return (home / "bin" / ("javac.exe" if os.name == "nt" else "javac")).exists()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--stage", type=Path, default=ROOT / "artifacts" / "jvm-host", help="folder to stage into")
    parser.add_argument("--test", action="store_true", help="also run the host's own tests")
    args = parser.parse_args()

    home = java_home()
    wrapper = HOST / ("gradlew.bat" if os.name == "nt" else "gradlew")
    tasks = ["stage"] + (["test"] if args.test else [])
    command = [str(wrapper), "--no-daemon", "--quiet", "--console=plain", *tasks, f"-PstageDir={args.stage.resolve()}"]
    environment = dict(os.environ, JAVA_HOME=str(home))
    print(f"build-jvm-host: building with {home} into {args.stage.resolve()}", flush=True)
    completed = subprocess.run(command, cwd=HOST, env=environment)
    if completed.returncode != 0:
        print("build-jvm-host: the host's Gradle build failed (a clean clone needs network access once for the "
              "Gradle distribution and the host's dependencies)", file=sys.stderr)
        return completed.returncode
    if not (args.stage / "host.jar").exists():
        print(f"build-jvm-host: the build left no host.jar in {args.stage}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
