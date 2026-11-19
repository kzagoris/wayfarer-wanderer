"""Keep vendor/passenger, Wayfarer's own copy of Passenger, in step with Passenger upstream.

vendor/passenger.json names the upstream repository, the upstream commit the copy was last brought to, and which of
upstream's paths the copy keeps ("keep" prefixes, less "drop" prefixes; a prefix ending in "/" is a folder). Everything
else upstream has (its tests, site, docs, scripts) is never copied.

  status        list where the copy differs from the recorded upstream commit (Wayfarer's own changes)
  diff [PATH]   show those differences as a unified diff
  update [REF]  bring the copy from the recorded commit to REF (default: upstream main), three-way merging upstream's
                changes into Wayfarer's; conflicting files are left with conflict markers to resolve by hand
  prune         delete files of the recorded commit that the keep rules no longer keep

The upstream history lives in artifacts/passenger-upstream.git (a mirror, made on first use), never in Wayfarer's own
history, and nothing is ever pushed anywhere.
"""
import argparse
import difflib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "vendor" / "passenger.json"
VENDOR = "vendor/passenger"
COPY = ROOT / VENDOR
CACHE = ROOT / "artifacts" / "passenger-upstream.git"


def git(*args, cwd=ROOT, data=None, check=True):
    result = subprocess.run(["git", *args], cwd=cwd, input=data, capture_output=True)
    if check and result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed:\n{result.stderr.decode(errors='replace')}")
    return result


def upstream(*args, data=None, check=True):
    return git(f"--git-dir={CACHE}", *args, data=data, check=check)


def load():
    return json.loads(MANIFEST.read_text(encoding="utf-8"))


def kept(manifest, path):
    def under(prefix):
        return path.startswith(prefix) if prefix.endswith("/") else path == prefix
    return any(map(under, manifest["keep"])) and not any(map(under, manifest["drop"]))


def ensure_cache(manifest, fetch):
    if not CACHE.exists():
        print(f"Cloning {manifest['repository']} into {CACHE.relative_to(ROOT)} ...")
        git("clone", "--mirror", "--quiet", manifest["repository"], str(CACHE))
    elif fetch:
        print(f"Fetching {manifest['repository']} ...")
        upstream("remote", "set-url", "origin", manifest["repository"])
        upstream("fetch", "--quiet", "--prune", "--tags", "origin")


def resolve(ref):
    result = upstream("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}", check=False)
    if result.returncode != 0:
        raise SystemExit(f"Upstream has no commit '{ref}'.")
    return result.stdout.decode().strip()


def tree(commit, manifest):
    """The kept blobs of an upstream commit: path -> blob id."""
    out = upstream("ls-tree", "-r", "-z", commit).stdout.decode()
    files = {}
    for entry in filter(None, out.split("\0")):
        meta, path = entry.split("\t", 1)
        mode, kind, blob = meta.split()
        if kind == "blob" and mode in ("100644", "100755") and kept(manifest, path):
            files[path] = blob
    return files


def local_hashes(paths):
    """The blob id each file of the copy would have in git, its attributes (eol=lf) applied: path -> id or None."""
    present = [p for p in paths if (COPY / p).is_file()]
    hashes = dict.fromkeys(paths)
    if present:
        out = git("hash-object", "--stdin-paths", data="\n".join(f"{VENDOR}/{p}" for p in present).encode()).stdout
        hashes.update(zip(present, out.decode().split()))
    return hashes


def local_files():
    """Every file of the copy git sees (tracked, or untracked and not ignored), relative to the copy."""
    out = git("ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", VENDOR).stdout.decode()
    return {p[len(VENDOR) + 1:] for p in filter(None, out.split("\0")) if (ROOT / p).is_file()}


def blob(blob_id, local=False):
    return (git if local else upstream)("cat-file", "blob", blob_id).stdout


def normalized(path):
    """A file of the copy as git would store it."""
    return blob(git("hash-object", "-w", f"{VENDOR}/{path}").stdout.decode().strip(), local=True)


def differences(manifest):
    base = tree(manifest["commit"], manifest)
    disk = local_files()
    hashes = local_hashes(sorted(base))
    modified = sorted(p for p, b in base.items() if hashes[p] is not None and hashes[p] != b)
    removed = sorted(p for p in base if hashes[p] is None)
    added = sorted(disk - set(base))
    return base, modified, removed, added


def status(args):
    manifest = load()
    ensure_cache(manifest, fetch=False)
    _, modified, removed, added = differences(manifest)
    print(f"vendor/passenger against upstream {manifest['commit'][:12]}:")
    for label, paths in (("modified", modified), ("added", added), ("removed", removed)):
        for path in paths:
            print(f"  {label:9} {path}")
    if not (modified or removed or added):
        print("  identical")


def diff(args):
    manifest = load()
    ensure_cache(manifest, fetch=False)
    base, modified, removed, added = differences(manifest)
    wanted = [p.replace("\\", "/").removeprefix(f"{VENDOR}/") for p in args.paths]
    for path in sorted(modified + removed + added):
        if wanted and not any(path == w or path.startswith(w.rstrip("/") + "/") for w in wanted):
            continue
        old = blob(base[path]).decode(errors="replace").splitlines(True) if path in base else []
        new = normalized(path).decode(errors="replace").splitlines(True) if path not in removed else []
        sys.stdout.writelines(difflib.unified_diff(old, new, f"upstream/{path}", f"{VENDOR}/{path}"))


def binary(*contents):
    return any(b"\0" in c[:8000] for c in contents)


def merge(path, ours, base, theirs):
    """Three-way merge into the copy's file: "clean", "conflict" (markers written) or "unmerged" (left as it was)."""
    if binary(ours, base, theirs):
        return "unmerged"
    with tempfile.TemporaryDirectory() as scratch:
        files = []
        for name, content in (("ours", ours), ("base", base), ("theirs", theirs)):
            file = Path(scratch) / name
            file.write_bytes(content)
            files.append(str(file))
        result = git("merge-file", "-p", "-L", "wayfarer", "-L", "upstream before", "-L", "upstream after", *files,
                     check=False)
    if result.returncode < 0 or result.returncode > 127:
        return "unmerged"
    (COPY / path).write_bytes(result.stdout)
    return "clean" if result.returncode == 0 else "conflict"


def update(args):
    manifest = load()
    ensure_cache(manifest, fetch=not args.offline)
    old = manifest["commit"]
    new = resolve(args.ref)
    if new == old:
        print(f"vendor/passenger is already at {new[:12]}.")
        return
    if upstream("merge-base", "--is-ancestor", old, new, check=False).returncode != 0:
        print(f"Note: {new[:12]} does not descend from {old[:12]}; merging their difference all the same.")
    before, after = tree(old, manifest), tree(new, manifest)
    changed = sorted(p for p in before.keys() | after.keys() if before.get(p) != after.get(p))
    outside = upstream("diff-tree", "-r", "--no-renames", "--name-only", "-z", old, new).stdout.decode()
    skipped = sum(1 for p in filter(None, outside.split("\0")) if not kept(manifest, p))
    hashes = local_hashes(changed)

    taken, merged, conflicts, unmerged, kept_ours = [], [], [], [], []
    for path in changed:
        base_id, new_id, ours_id = before.get(path), after.get(path), hashes[path]
        target = COPY / path
        if ours_id == base_id:  # Wayfarer never touched it: take upstream's version (or its deletion).
            taken.append(path)
            if args.dry_run:
                continue
            if new_id is None:
                target.unlink(missing_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(blob(new_id))
        elif ours_id == new_id:  # Already as upstream now has it.
            continue
        elif new_id is None:  # Upstream deleted a file Wayfarer changed: keep Wayfarer's.
            kept_ours.append(path)
        elif ours_id is None:  # Wayfarer deleted a file upstream changed: leave it deleted.
            kept_ours.append(path)
        else:
            if args.dry_run:
                merged.append(path)
                continue
            base = blob(base_id) if base_id else b""
            outcome = merge(path, normalized(path), base, blob(new_id))
            {"clean": merged, "conflict": conflicts, "unmerged": unmerged}[outcome].append(path)

    if not args.dry_run:
        prune_empty()
        manifest["commit"] = new
        MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    print(f"{'Would bring' if args.dry_run else 'Brought'} vendor/passenger from {old[:12]} to {new[:12]}:")
    print(f"  {len(taken)} file(s) taken as upstream has them")
    print(f"  {len(merged)} file(s) {'to merge' if args.dry_run else 'merged'} with Wayfarer's changes")
    print(f"  {skipped} upstream change(s) outside the kept paths, not copied")
    for path in kept_ours:
        print(f"  KEPT      {path}  (upstream changed or deleted it; Wayfarer's change or deletion stands: check by hand)")
    for path in conflicts:
        print(f"  CONFLICT  {path}  (conflict markers written)")
    for path in unmerged:
        print(f"  UNMERGED  {path}  (binary or unmergeable: Wayfarer's version left as it was, upstream's not applied)")
    if not args.dry_run:
        print("Next: resolve any conflicts, then `dotnet restore WayfarerWanderer.slnx --force-evaluate "
              "--configfile vendor/passenger/nuget.config`, build, test and commit.")
    if conflicts or unmerged:
        sys.exit(1)


def prune_empty():
    for folder, _, _ in sorted(os.walk(COPY), key=lambda w: len(w[0]), reverse=True):
        if folder != str(COPY) and not os.listdir(folder):
            os.rmdir(folder)


def prune(args):
    manifest = load()
    ensure_cache(manifest, fetch=False)
    out = upstream("ls-tree", "-r", "-z", "--name-only", manifest["commit"]).stdout.decode()
    gone = [p for p in filter(None, out.split("\0")) if not kept(manifest, p) and (COPY / p).is_file()]
    for path in gone:
        if not args.dry_run:
            (COPY / path).unlink()
    if not args.dry_run:
        prune_empty()
    print(f"{'Would delete' if args.dry_run else 'Deleted'} {len(gone)} file(s) the keep rules do not keep.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("status", help="list Wayfarer's changes to the copy").set_defaults(run=status)
    diff_parser = commands.add_parser("diff", help="show Wayfarer's changes to the copy")
    diff_parser.add_argument("paths", nargs="*")
    diff_parser.set_defaults(run=diff)
    update_parser = commands.add_parser("update", help="bring the copy to a newer upstream commit")
    update_parser.add_argument("ref", nargs="?", default="main", help="upstream branch, tag or commit (default: main)")
    update_parser.add_argument("--dry-run", action="store_true")
    update_parser.add_argument("--offline", action="store_true", help="use the cache as it is, without fetching")
    update_parser.set_defaults(run=update)
    prune_parser = commands.add_parser("prune", help="delete files the keep rules do not keep")
    prune_parser.add_argument("--dry-run", action="store_true")
    prune_parser.set_defaults(run=prune)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
