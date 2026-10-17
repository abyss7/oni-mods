#!/usr/bin/env bash
# Builds the simulation function hook engine (sim/hook) and the plugins (sim/plugins) for Windows x64
# and checks them without the game: under Wine against the real SimDLL.dll from the game folder.
#   sim/build.sh            build and check
#   sim/build.sh --no-test  build only
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
source "$here/../scripts/env.sh"
ZIG="$ONI_ROOT/tools/zig/zig"
[ -x "$ZIG" ] || { echo "Missing $ZIG: see scripts/build-native.sh" >&2; exit 1; }
export ZIG_GLOBAL_CACHE_DIR="$ONI_ROOT/tools/zig-cache" ZIG_LOCAL_CACHE_DIR="$ONI_ROOT/tools/zig-cache"
out="$here/build"; mkdir -p "$out"
minhook="$here/hook/third_party/minhook"

# In the mod folder the libraries are not stored as .dll: the game loads every *.dll there as a managed assembly.
build_library() { # name_without_extension sources...
  local name="$1"; shift
  "$ZIG" c++ -target x86_64-windows-gnu -O2 -fno-exceptions -fno-rtti -ffp-contract=off -Wno-nullability-completeness -shared \
    -I "$here/hook" -I "$minhook/include" -o "$out/$name.dll" "$@"
  mv "$out/$name.dll" "$out/$name.bin"
}
# Hook engine: one per game process, shipped in every mod that needs hooks.
build_library oni_simhook "$here/hook/simhook.cpp" \
  -x c "$minhook/src/hook.c" "$minhook/src/buffer.c" "$minhook/src/trampoline.c" "$minhook/src/hde/hde64.c"
# Plugins: each in its own library, a mod carries only its own. The engine test plugin is not put into mods.
build_library oni_gasmix "$here/plugins/gas_mix.cpp"
build_library oni_pressure "$here/plugins/pressure.cpp"
build_library oni_liquidmix "$here/plugins/liquid_mix.cpp"
build_library oni_selftest "$here/plugins/selftest.cpp"
rm -f "$out"/*.lib "$out"/*.pdb
"$ZIG" c++ -target x86_64-windows-gnu -O1 -o "$out/hooktest.exe" "$here/hook/test/hooktest.cpp"
echo "built: $out/oni_simhook.bin ($(stat -c %s "$out/oni_simhook.bin") bytes), $out/oni_gasmix.bin ($(stat -c %s "$out/oni_gasmix.bin") bytes)"

sim_dll="$ONI_GAME_DIR/OxygenNotIncluded_Data/Plugins/x86_64/SimDLL.dll"
symbols="$(ls "$here"/re/symbols/SimDLL-*.tsv 2>/dev/null | tail -1 || true)"
# Symbol table under the name the mod expects: it goes into the mod folder together with the library.
[ -n "$symbols" ] && cp "$symbols" "$out/sim_symbols.tsv"
[ "${1:-}" = "--no-test" ] && exit 0
# The gas mixture layer kernel does not depend on the game: it is built for this machine and checked against the Python model.
# Same flags as the plugin (any x86-64, no AVX): otherwise the step time in the check comes out better than in the game.
"$ZIG" c++ -target x86_64-linux-gnu -O2 -fno-exceptions -fno-rtti -ffp-contract=off -Wno-nullability-completeness -o "$out/kernel_check" "$here/model/kernel_check.cpp"
(cd "$ONI_ROOT" && python3 -I sim/model/kernel_check.py "$out/kernel_check")
# The liquid/vapour equilibrium rules do not depend on the game either.
"$ZIG" c++ -target x86_64-linux-gnu -O2 -fno-exceptions -fno-rtti -ffp-contract=off -Wno-nullability-completeness -o "$out/phase_check" "$here/model/phase_check.cpp"
"$out/phase_check"
# Liquid mixture rules: picking the substance by composition, density, inventory.
"$ZIG" c++ -target x86_64-linux-gnu -O2 -fno-exceptions -fno-rtti -ffp-contract=off -Wno-nullability-completeness -o "$out/liquid_check" "$here/model/liquid_check.cpp"
"$out/liquid_check"
# A separate Wine prefix: the game's prefix is left alone. It lives outside the project folder: the prefix has a
# dosdevices/z: link to the filesystem root, and editors and file indexers that follow links would walk the entire disk.
export WINEPREFIX="${XDG_CACHE_HOME:-$HOME/.cache}/oni-sim/wineprefix" WINEDEBUG=-all
to_win() { wine64 winepath -w "$1" 2>/dev/null; }
# Two passes: with a function table from the mod folder (if one is present) and with what the engine uses on the
# player's machine.
[ -n "$symbols" ] && { echo "-- using the function table"; wine64 "$out/hooktest.exe" "$(to_win "$sim_dll")" "$(to_win "$symbols")"; }
echo "-- using the game's files"
wine64 "$out/hooktest.exe" "$(to_win "$sim_dll")" "$(to_win "${sim_dll%.dll}.pdb")"
