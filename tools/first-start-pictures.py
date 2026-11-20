"""Take the first start's pictures of Wayfarer in each theme it carries, dark and light, into the app's assets.

Renders the normal window headlessly (WelcomeTests.TakeFirstStartPictures) with the Maven fixture open and writes
<theme>-dark.webp and <theme>-light.webp under src/WayfarerWanderer.App/Shell/Assets/FirstStart. Run it again after a
change to the look, the shell's layout or the themes carried, and commit the pictures with the change.
"""
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "WayfarerWanderer.App" / "Shell" / "Assets" / "FirstStart"
TESTS = ROOT / "tests" / "WayfarerWanderer.Tests"
CASE = "WayfarerWanderer.Tests.WelcomeTests.TakeFirstStartPictures"


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    before = {path.name: path.stat().st_mtime_ns for path in OUT.glob("*.webp")}
    env = dict(os.environ, WAYFARER_FIRST_START_PICTURES=str(OUT))
    subprocess.run(["dotnet", "build", str(TESTS), "-c", "Debug"], cwd=ROOT, check=True)
    subprocess.run(["dotnet", "test", str(TESTS), "-c", "Debug", "--no-build", "--filter", f"FullyQualifiedName={CASE}"],
                   cwd=ROOT, env=env, check=True)
    taken = sorted(path.name for path in OUT.glob("*.webp") if before.get(path.name) != path.stat().st_mtime_ns)
    if not taken:
        print("No picture was taken: the test did not run.", file=sys.stderr)
        return 1
    print("Taken:", ", ".join(taken))
    return 0


if __name__ == "__main__":
    sys.exit(main())
