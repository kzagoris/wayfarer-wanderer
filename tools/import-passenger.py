"""Keep vendor/passenger, Wayfarer's own copy of Passenger, in step with Passenger upstream.

vendor/passenger.json names the upstream repository, the upstream commit the copy was last brought to, and which of
upstream's paths the copy keeps ("keep" prefixes, less "drop" prefixes; a prefix ending in "/" is a folder). Everything
else upstream has (its tests, site, docs, scripts) is never copied.

  status        list where the copy differs from the recorded upstream commit (Wayfarer's own changes)
  diff [PATH]   show those differences as a unified diff
  update [REF]  bring the copy from the recorded commit to REF (default: upstream main), three-way merging upstream's
                changes into Wayfarer's; conflicting files are left with conflict markers to resolve by hand
  prune         delete files of the recorded commit that the keep rules no longer keep
  copies        list Wayfarer's hand-copied files whose Passenger originals changed upstream since they were copied
                (--to REF; default: the recorded commit); --diff FILE shows upstream's change to FILE's originals,
                --restamp FILE marks FILE as ported to REF

Wayfarer's shell (src/, mostly src/WayfarerWanderer.App/Shell/) holds hand-adapted copies of Passenger.App files, which
update never touches. Each starts with a stamp, a comment such as "From Passenger 2044e49b src/Passenger.App/X.cs, MIT":
the upstream commit it was copied at and the file(s) it came from. A bare name after a path is in that path's folder.

The upstream history lives in artifacts/passenger-upstream.git (a mirror, made on first use), never in Wayfarer's own
history, and nothing is ever pushed anywhere.

After an update, build and run the tests: NoPassengerInUiTests lists any upstream literal that would show "Passenger" in
Wayfarer's window. Edit it in vendor/passenger to say Product.Name (Wayfarer Wanderer at run time) or neutral words.
"""
import argparse
import difflib
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "vendor" / "passenger.json"
VENDOR = "vendor/passenger"
COPY = ROOT / VENDOR
CACHE = ROOT / "artifacts" / "passenger-upstream.git"
COPIES = "src"
STAMP = re.compile(r"Passenger ([0-9a-f]{7,40})\b")
TOKEN = re.compile(r"[\w./-]+\.(?:cs|axaml|xaml|json|xml|svg|md|txt|props|targets|py|js|ts|png|ico|ttf|otf)\b")


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


def stamped_files():
    """Every text file under src/ git sees that names a Passenger commit: path -> text."""
    out = git("ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", COPIES).stdout.decode()
    files = {}
    for path in sorted(filter(None, out.split("\0"))):
        file = ROOT / path
        data = file.read_bytes() if file.is_file() else b""
        if b"Passenger " in data and b"\0" not in data[:8000]:
            text = data.decode(errors="replace")
            if STAMP.search(text):
                files[path] = text
    return files


def stamps(text):
    """Each stamp in a file's text: (sha as written, the header after it to the end of its comment)."""
    matches = list(STAMP.finditer(text))
    for match, following in zip(matches, matches[1:] + [None]):
        before = text[text.rfind("\n", 0, match.start()) + 1:match.start()].lstrip()
        rest = text[match.end():following.start() if following else len(text)]
        if before.startswith("//"):
            lines = rest.split("\n")
            header = [lines[0]]
            for line in lines[1:]:
                if not line.lstrip().startswith("//"):
                    break
                header.append(line.lstrip()[2:])
            yield match.group(1), "\n".join(header)
        else:
            yield match.group(1), re.split(r"-->|\*/|\n\s*\n", rest, maxsplit=1)[0]


class Upstream:
    """Upstream commits, trees and changes, each read from the cache once."""

    def __init__(self):
        self.commits, self.trees, self.changes, self.ahead = {}, {}, {}, {}

    def commit(self, ref):
        if ref not in self.commits:
            result = upstream("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}", check=False)
            self.commits[ref] = result.stdout.decode().strip() if result.returncode == 0 else None
        return self.commits[ref]

    def tree(self, commit):
        """Every path of an upstream commit, and those paths by file name."""
        if commit not in self.trees:
            out = upstream("ls-tree", "-r", "-z", "--name-only", commit).stdout.decode()
            paths, names = set(filter(None, out.split("\0"))), {}
            for path in paths:
                names.setdefault(path.rsplit("/", 1)[-1], []).append(path)
            self.trees[commit] = paths, names
        return self.trees[commit]

    def changed(self, old, new):
        """What upstream changed from old to new, renames followed: path at old -> (path at new or None, +, -)."""
        if (old, new) not in self.changes:
            moved, counts = {}, {}
            names = upstream("diff-tree", "-r", "-M", "-z", "--name-status", old, new).stdout.decode().split("\0")
            i = 0
            while i + 1 < len(names):
                kind = names[i][:1]
                if kind in "RC":
                    if kind == "R":
                        moved[names[i + 1]] = names[i + 2]
                    i += 3
                    continue
                if kind != "A":
                    moved[names[i + 1]] = None if kind == "D" else names[i + 1]
                i += 2
            stats = upstream("diff-tree", "-r", "-M", "-z", "--numstat", old, new).stdout.decode().split("\0")
            i = 0
            while i < len(stats) and stats[i]:
                added, deleted, path = stats[i].split("\t", 2)
                if not path:  # A rename: its old and new paths follow.
                    path, i = stats[i + 1], i + 2
                counts[path] = (int(added) if added != "-" else 0, int(deleted) if deleted != "-" else 0)
                i += 1
            self.changes[old, new] = {p: (to, *counts.get(p, (0, 0))) for p, to in moved.items()}
        return self.changes[old, new]

    def is_ahead(self, commit, target):
        """Whether commit already descends from target: a file stamped past the target is not behind it."""
        if (commit, target) not in self.ahead:
            self.ahead[commit, target] = commit != target and upstream(
                "merge-base", "--is-ancestor", target, commit, check=False).returncode == 0
        return self.ahead[commit, target]


def under(folder, token):
    """Where a stamp's partial path may be: a bare name in the last full path's folder; a name with folders there or in
    a folder above it (Vcs/X.cs after src/Passenger.App/Containers/Y.cs is src/Passenger.App/Vcs/X.cs)."""
    parts = folder.split("/")
    return [folder] if "/" not in token else ["/".join(parts[:n]) for n in range(len(parts), 0, -1)]


def origins(path, text, up):
    """A stamped file's stamps resolved: [(sha as written, commit, [upstream paths])]. Warns of what resolves to nothing."""
    found = []
    for sha, header in stamps(text):
        commit = up.commit(sha)
        if commit is None:
            print(f"warning: {path}: upstream has no commit {sha} (fetch first?)", file=sys.stderr)
            continue
        paths, names = up.tree(commit)
        folder, resolved = None, []
        for token in TOKEN.findall(header):
            token = token.strip("./")
            if token in paths:
                resolved.append(token)
                folder = token.rsplit("/", 1)[0]
            elif folder and (near := next((f"{f}/{token}" for f in under(folder, token) if f"{f}/{token}" in paths),
                                          None)):
                resolved.append(near)
            elif "/" not in token and len(names.get(token, ())) == 1:
                resolved.append(names[token][0])
            else:
                print(f"warning: {path}: stamp {sha} names {token}, which upstream {sha} does not have", file=sys.stderr)
        if not resolved:
            print(f"warning: {path}: stamp {sha} names no upstream file", file=sys.stderr)
        found.append((sha, commit, list(dict.fromkeys(resolved))))
    return found


def behind(path, text, up, target):
    """Each origin of a stamped file as upstream has it at target: [{origin, stamp, status, renamed_to, added, deleted}],
    status one of unchanged, changed, renamed, deleted or ahead (stamped past the target)."""
    rows = []
    for sha, commit, paths in origins(path, text, up):
        ahead = up.is_ahead(commit, target)
        changes = {} if ahead else up.changed(commit, target)
        for origin in paths:
            to, added, deleted = changes.get(origin, (origin, 0, 0))
            status = ("ahead" if ahead else "unchanged" if origin not in changes else "deleted" if to is None
                      else "renamed" if to != origin else "changed")
            rows.append({"origin": origin, "stamp": sha, "status": status,
                         "renamed_to": to if status == "renamed" else None, "added": added, "deleted": deleted})
    return rows


def copy_path(name):
    """A Wayfarer file named on the command line: its path, and its name relative to the repository root."""
    file = Path(name).resolve()
    if not file.is_file():
        file = (ROOT / name).resolve()
    if not file.is_file():
        raise SystemExit(f"No file {name}.")
    try:
        return file, file.relative_to(ROOT).as_posix()
    except ValueError:
        return file, file.as_posix()


def copies(args):
    manifest = load()
    ensure_cache(manifest, fetch=args.fetch)
    up = Upstream()
    ref = args.to or manifest["commit"]
    target = up.commit(ref)
    if target is None:
        raise SystemExit(f"Upstream has no commit '{ref}'.")
    if args.diff:
        return copies_diff(args.diff, up, target)
    if args.restamp:
        return restamp(args.restamp, up, target)

    files = stamped_files()
    rows = {path: behind(path, text, up, target) for path, text in files.items()}
    late = {path: [r for r in found if r["status"] in ("changed", "renamed", "deleted")] for path, found in rows.items()}
    late = {path: found for path, found in late.items() if found}
    total = {path: (sum(r["added"] for r in found), sum(r["deleted"] for r in found)) for path, found in late.items()}
    order = sorted(late, key=lambda p: (-sum(total[p]), p))
    referenced = {(r["stamp"], r["origin"]) for found in rows.values() for r in found}
    changed = {(r["stamp"], r["origin"]): r for found in late.values() for r in found}
    added, deleted = sum(r["added"] for r in changed.values()), sum(r["deleted"] for r in changed.values())
    label = target[:12] if target.startswith(ref) else f"{ref} ({target[:12]})"
    summary = (f"{len(late)} of {len(files)} stamped file(s) behind upstream {label}: {len(changed)} of "
               f"{len(referenced)} origin(s) changed, +{added} -{deleted} line(s).")
    if args.json:
        json.dump({"ref": ref, "target": target, "stamped": len(files), "origins": len(referenced),
                   "files": [{"file": p, "added": total[p][0], "deleted": total[p][1], "origins": late[p]}
                             for p in order], "summary": summary}, sys.stdout, indent=2)
        print()
        return
    for path in order:
        print(f"+{total[path][0]:>5} -{total[path][1]:>5}  {path}")
        for r in late[path]:
            note = {"deleted": "  (deleted upstream)", "renamed": f"  (now {r['renamed_to']})"}.get(r["status"], "")
            print(f"{'':15}<- {r['origin']} @{r['stamp']}  +{r['added']} -{r['deleted']}{note}")
    print(summary)


def copies_diff(name, up, target):
    """Upstream's change, stamp to target, to a Wayfarer file's origins."""
    file, path = copy_path(name)
    found = origins(path, file.read_text(encoding="utf-8", errors="replace"), up)
    if not found:
        raise SystemExit(f"{path} has no Passenger stamp.")
    for sha, commit, paths in found:
        if up.is_ahead(commit, target):
            print(f"{path}: stamp {sha} is already past {target[:12]}.", file=sys.stderr)
            continue
        changes = up.changed(commit, target)
        for origin in paths:
            if origin not in changes:
                print(f"{path}: {origin} is unchanged from {sha} to {target[:12]}.", file=sys.stderr)
                continue
            to = changes[origin][0]
            out = upstream("diff", "-M", "--no-color", "--no-ext-diff", commit, target, "--", origin,
                           *([to] if to and to != origin else [])).stdout
            sys.stdout.flush()
            sys.stdout.buffer.write(out)


def restamp(name, up, target):
    """Rewrite a Wayfarer file's stamp shas to target's short sha, every other byte as it was."""
    file, path = copy_path(name)
    data, short, old = file.read_bytes(), target[:8], []
    for sha, commit, paths in origins(path, data.decode(errors="replace"), up):
        for origin in paths:
            if origin not in up.tree(target)[0]:
                to = None if up.is_ahead(commit, target) else up.changed(commit, target).get(origin, (None,))[0]
                print(f"warning: {path}: {target[:12]} has no {origin}{f' (it is {to} there)' if to else ''}: "
                      "edit the stamp's path by hand", file=sys.stderr)

    def stamp(match):
        if up.commit(match.group(1).decode()) is None:
            return match.group(0)
        old.append(match.group(1).decode())
        return b"Passenger " + short.encode()

    updated = re.sub(STAMP.pattern.encode(), stamp, data)
    if not old:
        raise SystemExit(f"{path} has no Passenger stamp.")
    if updated != data:
        file.write_bytes(updated)
    print(f"{path}: stamp {', '.join(dict.fromkeys(old))} -> {short}")


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
    copies_parser = commands.add_parser("copies", help="list Wayfarer's hand-copied files behind their originals")
    copies_parser.add_argument("--to", metavar="REF", help="upstream branch, tag or commit (default: the recorded commit)")
    copies_mode = copies_parser.add_mutually_exclusive_group()
    copies_mode.add_argument("--diff", metavar="FILE", help="show upstream's change, stamp to REF, to FILE's originals")
    copies_mode.add_argument("--restamp", metavar="FILE", help="rewrite FILE's stamps to REF once FILE is ported")
    copies_mode.add_argument("--json", action="store_true", help="print the listing as JSON")
    copies_parser.add_argument("--fetch", action="store_true", help="fetch upstream first")
    copies_parser.set_defaults(run=copies)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
