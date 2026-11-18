"""Inventory a Wayfarer publish and reject IDE, language-server and debugger payloads."""
import argparse
import json
import re
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("publish", type=Path)
parser.add_argument("inventory", type=Path)
args = parser.parse_args()
publish = args.publish.resolve()
manifest = publish / "WayfarerWanderer.App.deps.json"
deps = json.loads(manifest.read_text())
files = sorted(p.relative_to(publish).as_posix() for p in publish.rglob("*") if p.is_file())
libraries = sorted(deps["libraries"])
# Targets also name runtime assets which can be carried by an otherwise innocuous package.
assets = set()
for target in deps["targets"].values():
    for library in target.values():
        for section in ("runtime", "native", "runtimeTargets", "resources"):
            assets.update(library.get(section, {}))

forbidden = re.compile(
    r"(?:^|/)(?:Passenger(?:$|/|\.(?:dll|exe|App|Host|Lsp|Roslyn|DebugAdapter|DebugRelay|Debugger)\b)"
    r"|roslyn(?:/|$|-language-server|-server)|Microsoft\.CodeAnalysis\.LanguageServer"
    r"|(?:MP\.)?Microsoft\.CodeAnalysis\.(?:CSharp\.)?ExpressionEvaluator"
    r"|Microsoft\.Diagnostics\.DbgShim|(?:lib)?dbgshim\.|netcoredbg\b|vsdbg\b)", re.I)
matches = sorted(name for name in set(files) | set(libraries) | assets if forbidden.search(name)
                 and not name.startswith("notices/Passenger/"))
args.inventory.parent.mkdir(parents=True, exist_ok=True)
args.inventory.write_text(json.dumps(dict(publish=str(publish), files=files, libraries=libraries,
                                          runtime_assets=sorted(assets), forbidden=matches), indent=2) + "\n")
if matches:
    raise SystemExit(f"Forbidden payloads: {matches}")
print(f"{len(files)} files, {len(libraries)} libraries, {len(assets)} runtime assets, 0 forbidden")
