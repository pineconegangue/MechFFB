# MechFFB - Force Feedback for MechWarrior 5


https://github.com/user-attachments/assets/5458457d-3682-4a3d-bd14-708426aa1956


Force feedback system for MechWarrior 5 using DirectInput. 

## Quick Start

### If Using vJoy or any virtual controller:

**In MechFFB**: Select your actual device, do not select vJoy or any other virtual controller  
**In MW5**: You can still virtual controllers such as Joystick Gremlin or UCR for inputs

**YOU WILL NEED THE MECHSHAKER MOD FOR THIS TO WORK** - https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029


### Setup:
1. Download and set up MechShaker - https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029 - MechShaker app itself does not need to run for MechFFB to work, but if you have bass shakers this is non-negotiable - turn MechShaker on.
2. Download latest MechFFB release
3. Run MechFFB.exe
4. Select your FFB Joystick
5. Click "Test Device" to verify FFB output is actually working.
6. Click "Start Engine"
7. Launch MW5 and ensure MechShaker mod is enabled
8. Enable "Invert Axis" to invert force feedback vertical axis (only if your device requires this - weapon fire should pull towards you, not away from you - enable invert axis if weapon fire pulls away from you)
   
## Features

-  DirectInput backend (confirmed working on MOZA AB9, VPForce Rhino, yet to test other devices)
-  Weapon recoil effects
-  Damage impact effects
-  Simple intensity controls
-  Advanced per-weapon tuning
-  Movement/footstep effects
-  Destruction effects (when walking over mechs going critical, explosive infrastructure or stomping on tanks)
-  Settings autosave after adjustments

## Quirks/issues:
- The "Events" count on the right will always read 0 regardless of whether events are successfully being read - refer to the debug window instead
<img width="771" height="64" alt="image" src="https://github.com/user-attachments/assets/25de7cb7-3423-4048-bf03-6777d8169371" />

- In similar fashion, this text will still say "Waiting for Mechwarrior 5..." even after it has already successfully connected
- YAML and YAW introduce different firing modes for missiles. Streak LRMs can be fired as individual missiles and the FFB is mapped appropriately, but normal LRMs firing in salvos does not register separate events at this stage, so you will only get one 'missile firing' event.
- Laser 'duration' slider is a bit redundant for firing lasers - this is a residual feature but lasers now have their durations read and their outputs are adjusted accordingly.


## How it works (foundation built upon MechShaker)
MechShakerRelay, a blueprint mod for the game, gathers on-tick data and listens for a variety of in game events, packages up necessary details, and calls a parameterised OnTelemetry event that performs no immediate actions
MechShakerBridge, a C++ plugin, is injected into the game using one of two methods.
If using MechWarriorVR, as a UEVR plugin - https://github.com/sicsix/MW5-UEVR-Plugins
Otherwise, as a plugin for UnrealModLoader
MechShakerBridge, when using the UEVR plugin, hooks directly into the OnTelemetry blueprint event. It then writes this telemetry data out to a memory mapped file.
MechFFB reads this memory mapped file and converts in game events into force feedback output
