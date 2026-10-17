#!/usr/bin/env bash
# Builds the native modules for Windows x64 (the game runs under Proton): native/oni_native.cpp (OniFixes) and
# native/oni_light.cpp (OniLight), and runs their self-checks on Linux. Compiler: a local Zig from tools/zig, no administrator rights needed.
set -euo pipefail
source "$(dirname "$0")/env.sh"
ZIG="$ONI_ROOT/tools/zig/zig"
[ -x "$ZIG" ] || { echo "No $ZIG: download Zig 0.16.0 from ziglang.org and unpack it into tools/zig" >&2; exit 1; }
export ZIG_GLOBAL_CACHE_DIR="$ONI_ROOT/tools/zig-cache" ZIG_LOCAL_CACHE_DIR="$ONI_ROOT/tools/zig-cache"
out="$ONI_ROOT/native/build"; mkdir -p "$out"
flags="-O3 -mavx -fno-exceptions -fno-rtti"
# The light library is built for any x86-64 processor and chooses AVX at run time itself; no fused multiply-add, so
# both variants give the same numbers.
lightflags="-mcpu=x86_64 -O3 -fno-exceptions -fno-rtti -ffp-contract=off -std=c++20"

# Self-checks: the same logic, built for this machine.
"$ZIG" c++ $flags -o "$out/selftest" "$ONI_ROOT/native/selftest.cpp"
"$out/selftest"
"$ZIG" c++ $lightflags -o "$out/light_selftest" "$ONI_ROOT/native/light_selftest.cpp"
"$out/light_selftest" | tail -1

"$ZIG" c++ -target x86_64-windows-gnu $flags -shared -o "$out/oni_native.dll" "$ONI_ROOT/native/oni_native.cpp" "$ONI_ROOT/native/oni_textures.cpp"
# The game tries to load every *.dll from the mod folder as a managed assembly and on a native library
# refuses to load the whole mod. So the library goes into the mod under a different extension.
mv "$out/oni_native.dll" "$out/oni_native.bin"
"$ZIG" c++ -target x86_64-windows-gnu $lightflags -shared -o "$out/oni_light.dll" "$ONI_ROOT/native/oni_light.cpp"
mv "$out/oni_light.dll" "$out/oni_light.bin"
rm -f "$out"/*.lib "$out"/*.pdb
for lib in oni_native oni_light; do
  echo "built: $out/$lib.bin ($(stat -c %s "$out/$lib.bin") bytes)"
  objdump -p "$out/$lib.bin" | awk '/DLL Name/{print "depends on:", $3}'
  objdump -p "$out/$lib.bin" | awk '$NF ~ /^oni_/ && /base/ {print "export:", $NF}'
done
