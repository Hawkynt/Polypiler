#!/bin/sh
# fake-tool <text> [out|err] [seconds] [exit code]: waits the seconds, prints the text on standard
# output or standard error and exits with the code, as a toolchain's version command would.
if [ -n "$3" ] && [ "$3" != 0 ]; then sleep "$3"; fi
if [ "$2" = err ]; then printf '%s\n' "$1" >&2; else printf '%s\n' "$1"; fi
exit "${4:-0}"
