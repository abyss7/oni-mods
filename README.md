# Mods for Oxygen Not Included

Mods for Oxygen Not Included and the tools used to build, check and measure them.

## Game version

The mods are developed and tested against:

| | |
|---|---|
| Build | U59-744825 (Steam buildid 24423041), branch `public` |
| Platform | the Windows build of the game (on Linux it runs through Proton) |
| Engine | Unity 6000.3.5f2, Mono scripting backend (not IL2CPP) |
| Game code | `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll`, .NET Framework 4.8 |
| Patching | Harmony 2.4.2 ships with the game |
| DLC | tested with Spaced Out, DLC2, DLC4 and DLC5 |

Paths used by the build and the scripts (Linux with Steam and Proton; set in `Directory.Build.props` and `scripts/env.sh`):

- game: `~/.local/share/Steam/steamapps/common/OxygenNotIncluded`
- user data: `Documents/Klei/OxygenNotIncluded` (on Linux inside the Proton prefix: `~/.local/share/Steam/steamapps/compatdata/457140/pfx/drive_c/users/steamuser/Documents/Klei/OxygenNotIncluded`)
- dev mods: `<user data>/mods/Dev/<ModName>/`
- game log: `AppData/LocalLow/Klei/Oxygen Not Included/Player.log` (on Linux inside the Proton prefix)

## Project layout

```
Directory.Build.props     game paths, references to game assemblies, net48
Directory.Build.targets   deploys the mod to mods/Dev after the build
src/OniProfiler/          profiler mod: frame measurements, simulation reconnaissance, experiments
src/OniFixes/             mod with basic fixes and optimizations (settings via PLib)
src/OniGasMix/            "gas mixture" mod (settings via PLib; computation in the native plugin under sim/plugins)
src/OniPressure/          "pressure" mod: boiling, evaporation and condensation by pressure, heat of transition (computation in sim/plugins)
src/OniLiquidMix/         "liquid mixture" mod: own liquid movement, mixtures of liquids of one group, layers by density (computation in sim/plugins)
src/Common/               code shared by the mods: compiled into each mod separately
scripts/                  paths, native build, log and profiler report helpers
native/                   native C++ kernels (cell textures) and their self-check
sim/                      hooking of the native SimDLL simulation: hook engine, plugins, checks (see sim/README.md)
tools/                    local Zig for the native build (not in git)
```

## Five mods

Each large piece of work is a separate mod: it can be enabled, disabled and published independently of the others.

| Mod | What it does | What it carries besides its own assembly |
|---|---|---|
| `OniProfiler` | frame measurements by stage and method, hang watchdog; with the marker file `sim_probes.txt`: call counters for simulation functions | `oni_native.bin`, hook engine `oni_simhook.bin` |
| `OniFixes` | basic game fixes and optimizations: cell textures, item search, duplicant clothing, conveyor, shower, tool settings in the save | `PLib.dll`, `oni_native.bin` |
| `OniGasMix` | gas mixture: movement, pressure, breathing, sources and consumers, condensation, heat, germs, game logic by composition, tooltip and the "Gases" group in the Mixtures Overlay | `PLib.dll`, hook engine `oni_simhook.bin`, plugin `oni_gasmix.bin` |
| `OniPressure` | pressure: boiling, evaporation and condensation depend on pressure and come with heat of transition (in settings: pressure by gas mass or by molecules); pressure on walls is only measured so far (marker file `pressure_survey.txt`) | `PLib.dll`, hook engine `oni_simhook.bin`, plugin `oni_pressure.bin` |
| `OniLiquidMix` | liquid mixture: own liquid movement instead of the game's, mixtures of liquids of one mixing group, pumps and save by composition, freezing and boiling per liquid of the mixture, layers by density, water density anomaly, tooltip and the "Liquids" group in the Mixtures Overlay. Two mod settings (PLib window, both on by default): "Mixtures of liquids" (together with own movement) and "Layers by density" (with the water anomaly, works without mixtures too); with both off, only a liquid inventory to the log | hook engine `oni_simhook.bin`, plugin `oni_liquidmix.bin`, PLib |

Rules that keep the mods from getting in each other's way:

- **Mods do not depend on each other.** Shared code lives in `src/Common` and is compiled into each mod separately (the mod's project lists the files it needs), so each mod has its own copies and its own state: its own `ab_test.txt`, its own marker files and translations in its own folder. The shared code learns about the mod from `ModContext` (mod folder and name for the log), filled in by `Mod.OnLoad`.
- **There is one simulation hook engine in the game.** Its library ships in every mod that needs hooks, but it is loaded by whichever mod asks first; the others find the already loaded one by file name (`SimHooks.Load`). Two instances would install hooks on the same functions on top of each other. If the mods are built against different engine versions, the second one is refused and runs without hooks.
- **Mods carry no game files.**
- **A plugin is a separate library.** A mod carries only its own plugins (`SimHooks.LoadPlugin`); names of the plugin, its values and data start with the plugin name (`gas-mix.…`).
- **One C# game function is patched by one mod.** If two mods need the same function, the patch must live in one of them. The exception works the same way as the hook engine: the shared mixture interface (`src/Common/MixtureUi.cs`: the hover tooltip, the selected-cell panel line, the Mixtures Overlay, formerly the oxygen overlay) patches the game functions once, by whichever mod registered first, and the gas mixture and liquid mixture mods only add their groups to it ("Gases", "Liquids"). The group list lives in the application domain data and consists of standard library delegates, so copies of the shared code in different mods see the same list. There are no other overlaps: `OniFixes` does not touch the oxygen overlay (now the Mixtures Overlay; the gas choice in the save is stored by `OniGasMix`), the mixture mods do not touch what `OniFixes` patches.
- **Several plugins may hook one simulation function.** The engine links their hooks (and the profiler call counter from `sim_probes.txt`) into a chain: at the start of the function, one jump to the first link; the "original" of each link is the next link, and for the last one it is the function itself. Cost: one indirect call per plugin that needs this function; there is no intermediary, and with one plugin the path is the same as without chains. A plugin sets the order of links with a number (`SIM_HOOK_ORDERED`, smaller means closer to the game code); on equal numbers the plugin name decides, not the mod load order; the counter is always first. Plugins agree only on order: each must behave correctly if another plugin modifies the function before or after it.
- **A hook that looks at the return address is the only one on its function.** Several gas mixture layer hooks recognise by the return address which place in the game code called them (`DisplaceGas`, `CellSOA::SwapCells`, `GetReachableCells`). Such a hook is declared with `SIMHOOK_DIRECT` and stands first in the chain; a second one of the same kind on the same function will not be installed, and its plugin will not start (error in the mod log). If another plugin needs these functions in the same way, the address check in the layer has to be replaced with a flag set by a hook on the enclosing function.
- **Gas pressure in a cell is `Grid.Mass`, with or without the mixture mod.** `OniGasMix` gives the game the mass of the cell's whole mixture (adds the other gases to the main gas mass in the simulation data frame); the cell's element stays the main gas. So C# code that only needs the total gas mass (like `OniPressure`) does not need to know about the mixture mod. This array is not suitable in two cases: when the mass of a single gas is needed (only `OniGasMix` knows it: `GasMixture`) and in native code (the simulation's own cells still hold only the main gas).
- **Phase rules are computed by whoever owns the gas.** Boiling and condensation by pressure (`OniPressure`) need the pressure of a single gas and run in the simulation thread. Without the mixture mod a cell holds one gas, and the pressure plugin computes the rules in its own pass. With the mixture mod, the mixture layer computes them by partial pressures: `OniPressure` sets the value `gas-mix.phase` on the layer plugin and does not enable its own pass (together they would compute the rules twice). Both plugins use the same formulas (`sim/plugins/phase_rules.h`). The ban on the simulation's own liquid-gas transitions is set by `OniPressure` in both modes.
- **Each mod's strings live under its own root:** `STRINGS.ONIFIXES`, `STRINGS.ONIGASMIX`, `STRINGS.ONIPRESSURE`, `STRINGS.ONILIQUIDMIX`. The game sets up translations after the mods load, so strings that the shared code shows on behalf of a mod (Mixtures Overlay names, group headers) are passed as functions and fetched at display time.

## Commands

```bash
dotnet build -c Release                # build all mods and deploy to mods/Dev
dotnet build src/OniFixes -c Release   # one mod
dotnet build -c Release -p:NoDeploy=true   # build without deploying

scripts/log.sh            # mod lines, warnings and errors from Player.log (-f follow, -a everything)
scripts/profile.sh        # latest profiler report (-a whole file)
scripts/build-native.sh   # build and check the native module

steam -applaunch 457140   # launch the game
```

The description in `mod.yaml` (the game does not translate it) is in English.

New mod: a folder in `src/` with `Mod.cs`, `mod.yaml`, `mod_info.yaml` and a csproj modelled on `src/OniGasMix` (it also lists the needed files from `src/Common`), then `dotnet sln add`.

The game reads mod DLLs only at startup. After a rebuild the game must be restarted. A new mod appears in the mod list disabled: enable it once in the Mods menu.

## What can and cannot be changed

### Fully available: managed code (C#, via Harmony)

All game logic above the level of cell physics is written in C# and can be patched: prefix, postfix, transpiler, full method replacement.

- duplicant AI and chores: `Brain`, `ChoreConsumer`, `ChoreProvider`, `GlobalChoreProvider`, `FetchManager`
- pathfinding: `Pathfinding`, `NavGrid`, `NavGridUpdater`, `AsyncPathProber`, `PathFinder`
- networks: pipes and conveyors (`ConduitFlow`, `SolidConduitFlow`), power (`CircuitManager`), automation (`LogicCircuitManager`)
- buildings, critters, plants, diseases as game objects, state machines (`GameStateMachine`)
- update scheduler: `StateMachineUpdater`, `SimAndRenderScheduler` (interfaces `ISim33ms`, `ISim200ms`, `ISim1000ms`, `ISim4000ms`, `IRender*`)
- `KAnim` animations and their batching, UI, overlays
- saving and loading, world generation (`ProcGenGame`)
- what the game sends to the native simulation and when: 62 message types in `SimMessages`

Burst and Unity Jobs are not used in the game, and there is no hidden natively compiled game code. Multithreading is its own: `JobManager`, `GlobalJobManager`, `WorkItemCollection`. They are used by pathfinding, fetch chore distribution, pipes, property textures.

### Available through mod files, no code

The folders `elements/`, `worldgen/`, `templates/`, `codex/`, `strings/`, `anim/`, `translations/` in the mod directory are overlaid on the data from `StreamingAssets`.

### Not available from C#

- **`SimDLL.dll`**, the native C++ simulation. Inside it: gas and liquid movement, heat exchange, phase transitions, spread of germs and radiation across cells, pipe contents temperature (`ConduitTemperatureManager_*`). From C# only the input messages and the handling of results can be changed. The mods replace parts of this core game logic with their own native code through function hooks; the infrastructure for this is in `sim/`, and the gas mixture, pressure and liquid mixture mods are built on it.
- **`UnityPlayer.dll`**: rendering, physics, audio, garbage collector. Only what Unity exposes through its C# API can be configured.

### Harmony limitations you can trip over

- The JIT inlines small methods into the caller, and a patch on them silently does nothing. Patch the calling method instead.
- Methods of generic classes (`UpdateBucketWithUpdater<T>`, `UtilityNetworkManager<,>`) can only be patched for concrete closed types.
- `extern` methods (`SIM_HandleMessage` and others) cannot be patched; patch the managed wrapper around them.
- Other mods may patch the same methods, including chore distribution and fetching. Our mods are tested without other mods.

## Tool and overlay settings in the save

The game does not save tool modes and lists in overlay legends: after loading, "deconstruct buildings only" or "materials: gases" have to be chosen again. The `RememberToolSettings` fix ([src/OniFixes/UiSettings.cs](src/OniFixes/UiSettings.cs), on by default) writes them to the save and restores them after loading:

- for tools with a shared list of modes (deconstruct, cancel, dig, disable, empty pipe, priorities) and for harvest, which list rows are enabled is remembered;
- the same for overlays with a list in the legend (materials, temperature, germs); the overlay applies the mode itself when opened, as it does with the defaults;
- the oxygen overlay is not included: the game has no list in it, and with the gas mixture mod the gas choice is stored by that mod itself (in its own save component);
- the relative temperature slider position is saved by the game itself (`SaveGame.relativeTemperatureOverlaySliderValue`); only the overlay mode was lost, and without it the slider is not visible;
- which overlay was open is not remembered.

The data lives in the `UiSettingsState` component on the save object, as one JSON string; a save without the mod loads as usual (the game skips data of an unknown component). Self-check: the file `verify_ui_settings.txt` in the mod folder (with the word `set` it changes three settings and saves the game to `save_files/OniUiCheck/`, otherwise it logs the settings as they are in the game). Verified with two runs on 2026-10-12: the three changed settings and the modes of the other tools and overlays after loading are the same as written to the save.

## Mod strings

Everything a mod shows to the player is taken from the game's own strings where possible (gas names, units, words like "Mixture"): then the text comes in the game's language by itself. The mods have few strings of their own, and they work the same way as the game's strings:

- the English text lives in `LocString` fields of the mod's strings class: `ONIFIXES` ([src/OniFixes/ModStrings.cs](src/OniFixes/ModStrings.cs)), `ONIGASMIX` ([src/OniGasMix/ModStrings.cs](src/OniGasMix/ModStrings.cs));
- mods load translations from `translations/<language code>.po` in the mod folder if present (in the source tree `src/<mod>/translations/`, deployed to the mod folder on build); none are shipped;
- the string key in a translation file: `STRINGS.<class name>.<class>.<field>`, for example `STRINGS.ONIGASMIX.GASMIXTURE.OVERLAY_NAME`;
- after the game has chosen a language, the mod reads its file for that language, gives the translation to the fields and puts the texts into the game's shared string table ([src/Common/ModLocalization.cs](src/Common/ModLocalization.cs)). No file or no entry: the English stays.

A new string is a field in the class (and an entry in each `.po`, if there are any). To check a translation without changing the game language: a file `verify_strings_language.txt` with the language code in the mod folder.

Mod settings go the same way: in the mod's `Options.cs` each setting has the keys `STRINGS.<mod root>.OPTIONS.<setting>.NAME` and `.TOOLTIP` and a section key, and the settings window (PLib) finds the text by them in the game's string table.

## Native code in mods

A mod can compute over game data in C++ code. Sources are in `native/`; the library `oni_native.bin` is needed by `OniFixes` (texture kernels) and `OniProfiler` (trial kernels), each with its own copy in its folder:

- `native/oni_grid.h`: shared grid description for the kernels (`OniGridView`: sizes, active world, pointers to simulation and game arrays), the texture rectangle to write, result codes and the structure version number `ONI_NATIVE_ABI`;
- `native/oni_textures.cpp`: filling cell property textures (fog of war, digging, mass), the working kernels of the OniFixes mod;
- `native/oni_native.cpp`: trial kernels (grid summary in scalar and AVX variants, counting cells with an element);
- `native/selftest.cpp`: check on Linux without the game: kernels are compared with a direct transcription of the game logic, bad ranges must be rejected;
- `scripts/build-native.sh`: self-check and cross-compilation for Windows x64 with the local Zig from `tools/zig` (no administrator rights needed);
- `src/OniFixes/NativeLib.cs`: loader. `LoadLibrary`, structure version check, functions via `GetProcAddress`;
- `src/OniFixes/NativePropertyTextures.cs`: the fix that calls the texture kernels;
- `src/OniProfiler/NativeProbe.cs`: trial bridge in the profiler, compares the trial kernels with the same computation in C#.

Rules the first port was written by and that are worth keeping:

- **Fall back to the game code.** No library, it failed to load, wrong structure version, no grid data yet, the kernel rejected the range: in all these cases the original runs.
- **The kernel checks bounds itself.** Writing past a buffer crashes the game without a message, so the kernel receives the buffer size and writes nothing on a mismatch.
- **Data without copying.** Simulation arrays (`Grid.elementIdx`, `Grid.mass`, `Grid.properties`, background) are passed as pointers; the game's managed arrays (`Grid.WorldIdx`, `Grid.Visible`, `Grid.BuildMasks`, `Grid.Damage`) are pinned with `fixed` for the duration of the call. Element properties, which in the game live in `Element` objects, are exported into a flat table by element index.
- **Self-check against the original.** The file `verify_native_textures.txt` in the mod folder: after the kernel, the game code computes the same area, and the results are compared byte by byte (the number in the file: check every Nth call).
- **Kernels only read the grid** and write to the buffer they were given. The library has no state or threads of its own yet.

Result of the first port (run 20261010-233109, area of about 5800 cells): 0 differing bytes out of 3.8 million; 10-58 us per call versus 107-385 us for the single-threaded game code, i.e. 5-10 times faster. Per frame this is about 0.2-0.4 ms; a "before and after" comparison within a run cannot distinguish such an effect (31.43 and 31.32 ms).

### Background layer experiment (removed)

Before the gas mixture there was a measurement experiment: a field over all cells in native memory, which the library recomputed once per simulation tick in its own background threads, receiving a copy of the tick arrays from the main thread. The code was removed on 2026-10-12 (files `src/OniProfiler/NativeLayerProbe.cs`, `native/oni_layer.cpp`, `native/oni_pool.h`); the gas mixture mod works differently: it computes directly in the simulation thread. What the experiment showed:

- **Copying the tick arrays is cheap:** 0.25 ms of the main thread for 1.2 MB (grid 1114x178).
- **Own background threads do not speed up the step in the game:** 1111, 1282 and 1305 us on 1, 2 and 3 threads. A sleeping helper wakes up in 0.6-0.9 ms, almost the time of the whole step.
- **C# and C++ arithmetic does not match bit for bit:** the C# reference diverged from the kernel in 3.7% of cells by one least significant bit of the float. Hence the decision to compare with a tolerance.
- **An unfinished `std::thread` must not be destroyed when the game exits:** its destructor aborts the process.

Things to know:

- The library is built for Windows, like the game (on Linux the game runs through Proton).
- C++ standard library threads (`std::thread`, `std::mutex`, `std::condition_variable`) work under Proton; the build picks up no extra dependencies.
- The mod loader tries to load every `*.dll` in the mod folder as a managed assembly and, on a native library, refuses to load the whole mod. So the library is placed in the mod as `oni_native.bin`: `LoadLibrary` does not care about the extension.
- Vector instructions are limited to AVX, without AVX2 and FMA: players' processors vary, and older ones (Ivy Bridge) lack them. There are no 256-bit integer operations; integers are packed in 128-bit halves.
- Writing results through `uint8_t*` hampers the compiler: by the language rules such a pointer may point to anything. Kernel parameters must be copied into local variables, otherwise the loop bounds are re-read on every cell.
- Random test data inflates the time several times (unpredictable branches). Measure time on coherent data, as in the game, and make per-cell data choices with masks and tables.
- Only flat arrays are accessible from C++. Game objects (duplicants, buildings, chores) live in managed memory and stay on the C# side.

## Threads: what is tied to the frame and what is not

| Kind | What runs there | What happens under overload |
|---|---|---|
| Main thread, frame waits | Almost all logic: state machines and `ISim*`/`IRender*` components, duplicant and critter decisions, path movement, applying simulation results to objects, `KAnim` animations, UI | Frame rate drops |
| Parallel, but frame waits | Jobs via `GlobalJobManager.Run`: the main thread hands work to `KWorker` threads and waits for the end. This computes pipes (`ConduitFlow`), item fetching (`FetchManager`), chore precondition checks (`ChoreProvider`), navigation grid updates, `PropertyTextures`, auto-sweepers | Frame rate drops, but more slowly |
| Main thread with a quota | `BrainScheduler`: per frame, 1 duplicant brain and 5 critter brains are updated in rotation | The frame does not suffer; duplicants and critters make decisions less often |
| Background threads, frame does not wait | Native simulation (`Sim::Main` and its jobs in `SimDLL`, exchange via `FrameSync`), reachability computation `AsyncPathProber`, Unity render thread and the GPU, FMOD audio, network | The result arrives later: physics, paths and the picture lag behind the logic |

The render thread has back pressure: if the GPU cannot keep up, the main thread waits for it in `WaitForLastPresentationAndUpdateTime`.

The Unity API (scene objects, transforms, components, textures) is available only from the main thread. Only computations over plain arrays and the game's own structures can be moved to the background.

## Profiler

Klei's built-in profiler (`KProfiler`) is stripped from the release build by conditional compilation, and the Superluminal calls work only with the Windows profiler installed. So measurements are made by our own `OniProfiler` mod. It collects three kinds of data:

- **Frame stages.** Through the Unity `PlayerLoop` API, timestamps are inserted between all engine stages; game methods are not patched for this. The sum over stages equals the frame time, so everything is visible: scripts (`ScriptRunBehaviourUpdate`, `ScriptRunBehaviourLateUpdate`), rendering (`FinishFrameRendering`), UI (`PlayerUpdateCanvases`) and waiting for the GPU (`WaitForLastPresentationAndUpdateTime`).
- **Update buckets.** The loop `StateMachineUpdater.BucketGroup.AdvanceOneSubTick` is replaced with a copy that measures each bucket. All `ISim*`/`IRender*` components and state machine Update callbacks go through buckets; the bucket name matches the type or state name.
- **Individual methods** from `profile_methods.txt` (in the mod folder, edited without rebuilding).
- **All per-frame handlers** (`Update`, `LateUpdate`, `FixedUpdate`) via the line `@unity-callbacks N` in the method list: wrappers are installed in-game in batches of N, one batch per report window, and removed before exit.

The first 15 seconds after loading a save are skipped; after that, every 30 seconds a report is appended to `mods/Dev/OniProfiler/profile.log`.

The `Sim.HandleMessage` line shows how much time the main thread spends in the native simulation. This is the part of the frame a mod cannot affect.

Enable the profiler only for measurements.

### What must not be done

Do not wrap per-frame handlers with measurements when the mod loads: the game crashes on going to the main menu (cause not established). Wrappers installed in-game and removed before a scene change (the `@unity-callbacks` and `@ui-callbacks` modes) are unreliable: once a full pass of six batches went cleanly, and then three runs in a row failed right after the first batch was installed. Twice it was an exception on entering a freshly wrapped method (`PopFX.Update`, `CanvasUpdateRegistry.PerformUpdate`), once the main thread looped. Cause not established; between the successful and failed runs, conveyor fixes were added to OniFixes, the connection was not checked. Until the cause is found, these modes cannot be relied on: take the cost of per-frame handlers from the frame stages.

For hangs, the profiler has a watchdog (`Watchdog.cs`): if a frame in the game stalls for more than 20 seconds, it writes to `hang.txt` at which stage and in which bucket the frame stalled, plus the managed thread stacks, and in an automated run also terminates the game.

## Self-checks

Most changes have a self-check that runs in the game: a marker file in the mod folder (for example `verify_liquid_cases.txt`, `verify_gas_cases.txt`, `verify_conveyor.txt`) makes the mod build test scenes in the active world, point the camera, switch overlays or take screenshots, and write "ok" or "FAILED" with numbers to the log. The scenes change the world, so run them on a copy of a save. Without the game, `sim/build.sh` checks the hook engine and the plugins, and `scripts/build-native.sh` checks the native kernels.

## License

Copyright 2026 Ivan Lezhankin. Licensed under the Apache License, Version 2.0 (`LICENSE`). If you redistribute this code or build on it (for example, publish a mod based on it on the Steam Workshop), keep the attribution in `NOTICE`: credit the author and link to https://github.com/abyss7/oni-mods. Third-party code: MinHook (BSD 2-Clause, `sim/hook/third_party/minhook`); the mods ship PLib (MIT) from NuGet. Oxygen Not Included is a trademark of Klei Entertainment; this project is not affiliated with Klei, and no game code or assets are included.
