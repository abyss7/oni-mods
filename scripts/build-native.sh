#!/usr/bin/env bash
# Builds the native module native/oni_native.cpp for Windows x64 (the game runs under Proton)
# and runs its self-check on Linux. Compiler: a local Zig from tools/zig, no administrator rights needed.
set -euo pipefail
source "$(dirname "$0")/env.sh"
ZIG="$ONI_ROOT/tools/zig/zig"
[ -x "$ZIG" ] || { echo "No $ZIG: download Zig 0.16.0 from ziglang.org and unpack it into tools/zig" >&2; exit 1; }
export ZIG_GLOBAL_CACHE_DIR="$ONI_ROOT/tools/zig-cache" ZIG_LOCAL_CACHE_DIR="$ONI_ROOT/tools/zig-cache"
out="$ONI_ROOT/native/build"; mkdir -p "$out"
flags="-O3 -mavx -fno-exceptions -fno-rtti"

# Self-check: the same logic, built for this machine.
"$ZIG" c++ $flags -o "$out/selftest" "$ONI_ROOT/native/selftest.cpp"
"$out/selftest"

"$ZIG" c++ -target x86_64-windows-gnu $flags -shared -o "$out/oni_native.dll" "$ONI_ROOT/native/oni_native.cpp" "$ONI_ROOT/native/oni_textures.cpp"
# The game tries to load every *.dll from the mod folder as a managed assembly and on a native library
# refuses to load the whole mod. So the library goes into the mod under a different extension.
mv "$out/oni_native.dll" "$out/oni_native.bin"
rm -f "$out"/*.lib "$out"/*.pdb
echo "built: $out/oni_native.bin ($(stat -c %s "$out/oni_native.bin") bytes)"
objdump -p "$out/oni_native.bin" | awk '/DLL Name/{print "depends on:", $3}'
objdump -p "$out/oni_native.bin" | awk '$NF ~ /^oni_/ && /base/ {print "export:", $NF}'
