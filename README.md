# MechFFB

Force feedback effects for MechWarrior 5, built on top of [MechShaker](https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029).

---

## ⚠️ Requirement

**MechShaker is required for MechFFB to work.**
Download it here: https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029

MechShakerRelay **must** be active. The MechShaker app itself doesn't need to be running — but if you have bass shakers, you should run it too.

---

## Setup

1. **Download and configure MechShaker**
   Get it from https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029 and ensure MechShakerRelay is active.

2. **Download the latest MechFFB release**
   Extract it anywhere you like.

3. **Replace MechShakerRelay.pak**
   In your MW5 mods folder, navigate to `MechShakerRelay > Paks` and replace `MechShakerRelay.pak` with the one included in the MechFFB release `.zip`.

4. **Run MechFFB.exe**

5. **Select your FFB joystick**
   Do not select a virtual controller (e.g. vJoy) — select your actual physical device.

6. **Click "Test Device"**
   Verifies that FFB output is working. This can be finicky on some devices — don't worry if it doesn't respond.

7. **Click "Start Engine"**

8. **Launch MW5** and confirm the MechShaker mod is enabled.

9. **Configure Invert Axis if needed**
   Weapon fire should pull the stick *towards* you. If it pushes *away*, enable "Invert Axis".
   *(Currently confirmed working on Moza AB9 for v3.0.0)*

---

## Virtual Controller Note

If you use vJoy or another virtual controller:

- In **MechFFB**: select your real physical device — do *not* select vJoy or any virtual controller.
- In **MW5**: virtual controllers (e.g. Joystick Gremlin, UCR) can still be used for inputs as normal.

---

## Features

- DirectInput backend *(confirmed on: MOZA AB9, VPForce Rhino, Microsoft Sidewinder — others untested)*
- Weapon recoil effects
- Damage impact effects with directional feedback
- Movement / footstep effects
- Destruction effects (critical mechs, explosive infrastructure, stomping tanks)
- Simple intensity controls
- Advanced per-weapon tuning
- Settings auto-save after adjustments

---

## Known Quirks

- The **Events** counter on the right always shows `0`, even when events are being read successfully — check the debug window instead.
- The status text will continue to say **"Waiting for MechWarrior 5..."** even after a successful connection.
- YAML and YAW introduce different missile firing modes. Streak LRMs firing as individual missiles are mapped correctly. Normal LRMs firing in salvos only register as a single event — not per missile.

---

## How It Works

MechFFB is built on top of the MechShaker foundation:

1. **MechShakerRelay** (blueprint mod) collects per-tick game data, listens for in-game events, packages the relevant details, and fires a parameterised `OnTelemetry` event.

2. **MechShakerBridge** (C++ plugin) is injected into the game via one of two methods:
   - As a **UEVR plugin** if using MechWarriorVR — see https://github.com/sicsix/MW5-UEVR-Plugins
   - As a plugin for **UnrealModLoader** otherwise

3. MechShakerBridge hooks into the `OnTelemetry` event and writes telemetry data to a **memory-mapped file**.

4. **MechFFB** reads that memory-mapped file and converts in-game events into force feedback output.
