# MechFFB - Force Feedback for MechWarrior 5

Force feedback system for MechWarrior 5 using SDL2 (same as DCS World).

## Quick Start

### If Using vJoy + Joystick Gremlin:

**In MechFFB**: Select your actual device, do not select vJoy or any other virtual controller  
**In MW5**: You can still use vJoy/Joystick gremlin ETC for inputs

### Setup:

1. Build and run MechFFB.exe
2. **SDL2.dll** - I have added this to this repo, move it to same folder as the .exe after compiling
3. Select your FFB Joystick
4. Click "Test Device" to verify FFB output is actually working.
5. Click "Start Engine"
6. Launch MW5 and ensure MechShaker mod is enabled

## Features

-  SDL2 backend (same as DCS)
-  Weapon recoil effects
-  Damage impact effects
-  Simple intensity controls
-  Advanced per-weapon tuning
-  Movement/footstep effects 

Quirks:
The "Events" count on the right will always read 0 regardless of whether events are successfully being read - refer to the debug window instead
<img width="1891" height="57" alt="image" src="https://github.com/user-attachments/assets/9b171e7e-1c2e-4be6-aa54-4935a9090dcf" />
YAML and YAW introduce different firing modes for missiles. Streak LRMs can be fired as individual missiles and the FFB is mapped appropriately, but normal LRMs firing in salvos does not register separate events at this stage.
Laser 'duration' slider is a bit redundant - this is a residual feature but lasers now have their durations read and their outputs are adjusted accordingly.
