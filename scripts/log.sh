#!/usr/bin/env bash
# The game's Player.log. No arguments: mod lines, warnings and errors. -f: follow. -a: the whole log.
source "$(dirname "$0")/env.sh"
case "${1:-}" in
  -f) tail -n 200 -F "$ONI_PLAYER_LOG" ;;
  -a) cat "$ONI_PLAYER_LOG" ;;
  *)  grep -nE '\[Oni[A-Za-z]+\]|Loading MOD dll|\[WARNING\]|\[ERROR\]|Exception|Subscribe to mod|Build: ' "$ONI_PLAYER_LOG" ;;
esac
