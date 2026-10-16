# sim: hooking the native simulation

The cell simulation (gases, liquids, heat, pressure) lives in the game's native library `SimDLL.dll`. It cannot be changed from C#: Harmony patches only managed code. This part of the project provides hooking: our own native library that, inside the running game, replaces chosen simulation functions with ours (plugins) or counts their calls.

## Layout

```
sim/
  hook/         hook engine and MinHook in third_party (BSD license): library oni_simhook.bin
  plugins/      plugins: our versions of simulation functions, each in its own library (oni_gasmix.bin,
                oni_pressure.bin, oni_liquidmix.bin)
  model/        models and checks that run without the game
  build.sh      builds the engine and plugins and runs the checks without the game
  build/        build output, outside git
```

## Hooking

The engine `oni_simhook.bin` is loaded by a mod into the game process (`src/Common/SimHooks.cs`). In the game there is one engine for all our mods: the library ships in every mod that needs hooks (`OniGasMix`, `OniProfiler`), the first one to ask loads it, the others find the already loaded one by file name. It works like this:

- finds the loaded `SimDLL.dll` and the names and addresses of its functions, after verifying that they belong to this build of the library; on a mismatch it hooks nothing;
- takes the address by function name and replaces the start of the function with a jump to our code (MinHook itself moves the overwritten instructions into a "trampoline" through which the original is called).

Two kinds of hook:

- **Probe** (`simhook_probe_add`): counts calls and goes on to the original. The function's parameters need not be known, so it suits exploration.
- **Plugin**: our function with the same parameter list. It may call the original, change how it works, or not call it.

### Several hooks on one function

One simulation function may be hooked by several plugins and a probe at once. There is no separate intermediary library for this: the part shared by all mods is the engine itself, and it links the hooks of one function into a chain.

- The start of the function holds one jump: to the first link of the chain. The "original" of each link (`Name_original`) is the next link; for the last one it is the real continuation of the function. The jump at the function start is rewritten only when the first link changes; for the others the engine just rewrites the address of the next one.
- Cost: one indirect call per plugin that hooked this function. There is no dispatcher, no handler list and no per-call checks; a plugin that does not need the function does not take part in its chain. With one plugin on a function the call path is the same as before chains existed.
- Order: the number `order` (`SIM_HOOK_ORDERED(plugin, order, ...)`; for `SIM_HOOK` it is 0), lower means closer to the game code; on equal numbers the plugin whose name is alphabetically smaller comes first. The order does not depend on the order in which mods load or plugins are enabled. What a link does before calling the original runs in chain order, and what it does after runs in reverse.
- The probe is always first: it counts all calls of the function, even those a plugin never passed on to the original. The probe does not call the next link but jumps into it, so it does not change the return address.
- `SIMHOOK_DIRECT`: the order for a hook that uses the return address to find out which place in the game code called it. It stands first among plugins (after another plugin it would see an address inside that plugin). There cannot be two of these on one function: the second plugin will not be enabled, and the error names both. The gas mixture layer declares `DisplaceGas`, `CellSOA::SwapCells` and `GetReachableCells` this way.
- A disabled plugin leaves the chains; its `Name_original` then leads straight into the game function (the plugin may call it from outside a hook as well).

What the chain does not solve: meaning. Two plugins that modify one function must be written with each other in mind: for example, the pressure plugin's own pass and the gas mixture layer can technically both sit on `SimBase::UpdateData`, but the phase rules would then be computed twice, so the pressure mod does not enable its own pass next to the layer.

### How to write a plugin

A plugin is a separate library: a file `sim/plugins/name.cpp` and a `build_library` line in `sim/build.sh`. The mod ships it (`ModExtraFiles` in the mod project) and attaches it to the engine: `SimHooks.LoadPlugin("oni_name.bin", out error)`. The engine passes the plugin a table of its functions (`SimHookApi`), so the plugin does not depend on the engine library by name.

```cpp
#include "simhook_plugin.h"

SIM_PLUGIN_MODULE(); // once per plugin library: its attachment point to the engine

// Plugin name, result type, simulation function name, own name, parameters.
SIM_HOOK("my-plugin", float, "DoGasPressureDisplacement", GasDisplacement,
         (uint16_t element, int32_t cell, int32_t from, int32_t to, void* sim))
{
	return GasDisplacement_original(element, cell, from, to, sim);
}
```

Parameters must match those of the simulation function. 64-bit Windows programs have a single calling convention; for class methods the object pointer is the first parameter. Functions with the same name are distinguished as `name#1`, `name#2` (by ascending address).

From C# a plugin is enabled via `SimHooks.EnablePlugin("my-plugin", true, out error)` (`src/Common/SimHooks.cs`), probes via `SimHooks.AddProbe`. The profiler places probes on the functions listed in `sim_probes.txt` in its mod folder and writes the totals to the log on exit.

### Rules

- **Install and remove hooks only while the simulation is not running.** Currently that is mod load time: `SimHooks.Load` loads `SimDLL.dll` itself earlier than the game would.
- **Plugin code runs in simulation threads.** It must not call C#. Exchange with the mod only via atomic counters and flags.
- **A plugin is not alone on a function.** Before and after it another plugin may modify the function: do not rely on `Name_original` being the game function itself or on the return address lying in game code (for the latter there is `SIMHOOK_DIRECT`).
- **A parameter mistake crashes the game without a message.** Check the parameter list carefully, and run a plugin for the first time on a copy of a save.
- **After a game update** the function list no longer matches, and the library refuses to hook anything on its own. The plugins must be reviewed.

### Check without the game

After building, `sim/build.sh` runs `hooktest.exe` under Wine in a separate prefix (`~/.cache/oni-sim/wineprefix`, outside the project folder: a Wine prefix contains a link to the disk root, which editors and file indexers would follow). The test loads the real `SimDLL.dll`, places a probe on its own function and checks the count, enables and disables a test plugin, builds a chain of a probe and four plugins on its own function (link order, refusal of a second `SIMHOOK_DIRECT` hook, a link leaving the chain, call counts), and then installs and removes a probe on every function of the library. Simulation functions are not called in the process.

Result on build 744825: hooks install on 4821 functions; 200 are skipped as same-named (they must be addressed with a number), 15 are too short for a jump. All hooks are removed without leftovers.

### Plugin switches and counters

A plugin declares named numbers with the `SIM_FLAG` and `SIM_COUNTER` macros; the mod reads and writes them by name (`SimHooks.SetValue`, `SimHooks.GetValue`). This way a plugin is switched on and off on the fly without removing hooks: the hooks themselves stay installed from mod load, and in the off state they simply call the original.

## Status

- The hook engine runs in the game (checked on the Windows build under Proton on Linux); hooks stay installed for the whole session.
- Plugin `gas-mix` (`sim/plugins/gas_mix.cpp`): the gas mixture layer of `OniGasMix`. It moves gases itself (pressure, diffusion, gravity, spreading along ceilings and floors), keeps the mass of each gas per cell and computes the phase rules for mixtures.
- Plugin `pressure` (`sim/plugins/pressure.cpp`): boiling, evaporation and condensation by pressure with heat of transition for `OniPressure`; with the gas mixture layer the rules are computed by the layer.
- Plugin `liquid-mix` (`sim/plugins/liquid_mix.cpp`): own liquid movement, mixtures of liquids of one mixing group, layers by density, the water density anomaly, freezing, boiling and conversions by composition for `OniLiquidMix`.
- `sim/build.sh` checks the engine and all plugins without the game (`hooktest.exe` under Wine) and the models in `sim/model`.
