# Train Parameter Mod

![Train Parameter Mod](assets/train-parameter-mod.jpg)

A standalone C# mod for **Captain of Industry** with six adjustable train
parameters in a live in-game window. Defaults preserve vanilla behavior.
Built against **0.8.7d / Update 4**. Experimental game APIs may change in later versions.

**Validation status:** build and 48 headless integration checks pass against the
installed game assemblies, including the menu constructor hook, native F8
registration, simulation-side application and saved-value round-trip. Rendering
and interaction of the live window in the running game still need manual validation.

| Setting | Range | Effect |
| --- | --- | --- |
| Curve slowdown factor | 0–5 | 1 = vanilla; 0 = no curve-specific speed cap; 0.5 = double the curve limit; 2 = half |
| Acceleration multiplier | 0.1–10 | Scales positive acceleration; braking is unchanged |
| Wagon capacity multiplier | 0.1–10 | Multiplies cargo capacity in addition to difficulty/research modifiers |
| Speed multiplier | 0.25–5 | Scales train top speed and rail limits; engine power can still limit actual speed |
| Loading speed multiplier | 0.1–10 | Scales cargo quantity per loading batch |
| Unloading speed multiplier | 0.1–10 | Scales cargo quantity per unloading batch |

All values default to **1**. Settings are stored by the game with the save.
Changes apply during the current session, including to existing trains and wagons.
Lowering capacity retains existing cargo; an over-capacity wagon can unload normally
and cannot accept more cargo until space becomes available.

Signals, braking distances, station/depot maneuvering limits, connection animations,
transfer batch timing, supply availability, and storage limits remain in effect.
The global engine speed ceiling still applies. Route estimates and train performance
charts may use cached vanilla data. Very large multipliers can stress game physics.

## Build with dotnet

On Windows, install the **.NET 10 SDK** and own an installed Steam copy of the game.
The mod targets **.NET Framework 4.8**; reference assemblies are restored via NuGet.
The headless test runner uses .NET 10, which supports the default interface methods
in the game's UI assemblies. It does not launch or control the game.
Visual Studio and Unity are not required.

```powershell
$env:COI_ROOT = 'C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry'
dotnet build TrainParameterMod.sln -c Release
./scripts/Build.ps1
```

`Build.ps1` builds the solution, runs integration checks against your installed game
assemblies, and creates `dist/TrainParameterMod-0.1.0.zip`.
Pass `-GameRoot 'D:\SteamLibrary\steamapps\common\Captain of Industry'` for a custom install.

To build, check and install into your own mods directory:

```powershell
./scripts/Build.ps1 -Install
```

## Play

1. Extract the release ZIP into `%APPDATA%\Captain of Industry\Mods`.
   The resulting folder must be `Mods\TrainParameterMod`, containing `manifest.json`.
2. Start or restart the game. Select **Train Parameter Mod** when creating a game
   or adding mods to a save.
3. In a running game, press **F8**, or use **Escape → Train Parameter Mod (F8)**.
   Edit the six parameters and click **Apply**. Changes are submitted to the next
   simulation callback, including while paused; no save, exit or reload is required.
   Opening with F8 does not pause the simulation. Apply keeps the window open so
   you can adjust values repeatedly while observing your trains.
4. **Cancel / Close**, the window's close button or Escape discard edits made
   since your last Apply. Already applied changes remain active. **Restore defaults**
   prepares vanilla values, which are applied only after clicking **Apply**.
   Settings can also be chosen using the wrench next to the mod before starting.

After installing an updated mod DLL, restart the game once to load the new code.
Subsequent parameter changes use the live window.

Try a separate test save first. This mod does not add content or unlock DLC trains.
It modifies the trains available in your game. Removing it from a modded save is
deliberately not advertised as safe: use defaults of 1 to restore standard behavior.
Errors are logged to `%APPDATA%\Captain of Industry\Logs`.

## Implementation

The native `ModJsonConfig` provides save persistence and native input controls.
A native `WindowController` registers F8 with `IUnityInputMgr`. The pause menu has
an additional entry to open the same window. A separate draft is initialized from
an immutable settings snapshot. Apply queues a new snapshot; `ISimLoopEvents.UpdateBeforeCmdProc`
updates mod config and train values on the simulation thread, including while paused.
Input handlers do not mutate train/prototype state. Closing the menu removes no
previously applied changes, and unloading unregisters the shortcut and patches.
The implementation uses these game API points:

| Parameter | Game model / hook |
| --- | --- |
| Curve / rail limits | `EntityWithTrainTrackBaseProto.MaxSpeedTilesPerTick`, restricted to `TrainTrackProto`; `IsStraight` distinguishes curves |
| Acceleration | Positive results of `TrainStaticData.ScaleAccelerationBasedOnFunFactor` |
| Capacity | An owned delta modifier on `IdsCore.PropertyIds.TrainsCapacityMultiplier`; updates `CargoWagonProto.Capacity` and `SubCarCapacity` |
| Top speed | `Train.updateCachedValues`, derived from `TrainStaticData.MaxForwardsSpeedCombined` / `MaxBackwardSpeedCombined`; manual target speed is capped consistently |
| Loading / unloading | `TrainStationModule.TransferQuantity`, selected by `IsForLoading` |

Harmony applies small runtime hooks. No game DLLs, original game assets, decompiled
game implementations, private project code, credentials or machine configuration
are included. Building requires your own game installation.

## Artwork and licenses

The original mod artwork was generated with Google's `gemini-3.1-flash-image`.
`assets/thumbnail.png` is the in-game mod thumbnail. The optional
`scripts/Generate-Art.ps1` regenerates the source artwork using `GEMINI_API_KEY`;
building and playing the mod make no network requests.

Original mod code is MIT licensed. Harmony is distributed under its own MIT license
(see `THIRD-PARTY-NOTICES.txt`). This project is an independent community mod;
Captain of Industry and its materials belong to MaFi Games. Their
[Modding Policy](https://coigame.com/Legal/Modding-Policy) governs use with the game,
including free distribution of mods.

Official references: [modding guide](https://coigame.com/Modding),
[configuration](https://coigame.com/Modding/Configuration),
[example project](https://github.com/MaFi-Games/Captain-of-industry-modding).
