"""PreToolUse hook: refuse Bash commands too long for Git Bash on Windows.

Every Bash command over about 7,500 characters failed in this repo's sessions with "unexpected EOF while looking for
matching `''", whatever the quoting; every one under about 6,800 ran. Those were heredocs carrying whole files or
Python edit scripts. Refusing early saves the wasted call and points at the tools that have no limit.
"""
import json
import sys

LIMIT = 6000

call = json.load(sys.stdin)
command = call.get("tool_input", {}).get("command", "")
if len(command) > LIMIT:
    sys.stderr.write(
        f"Bash command is {len(command)} characters; Git Bash on Windows cuts commands near 7,500, so it would fail "
        "with 'unexpected EOF'. Write new files with the Write tool and change existing ones with Read + Edit. "
        "Python's open(p, 'w') on Windows also writes CRLF into this LF repo.\n"
    )
    sys.exit(2)
