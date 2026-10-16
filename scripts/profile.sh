#!/usr/bin/env bash
# OniProfiler report. No arguments: the last measurement window. -a: the whole file.
source "$(dirname "$0")/env.sh"
f="$ONI_DEV_MODS_DIR/OniProfiler/profile.log"
[ -f "$f" ] || { echo "No report yet: enable the OniProfiler mod and play for 30 seconds ($f)" >&2; exit 1; }
if [ "${1:-}" = "-a" ]; then cat "$f"; else awk '/^===== /{buf=""} {buf=buf $0 "\n"} END{printf "%s", buf}' "$f"; fi
