Setup guide:

If Using vJoy or any virtual controller:

In MechFFB: Select your actual device, do not select vJoy or any other virtual controller

In MW5: You can still virtual controllers such as Joystick Gremlin or UCR for inputs

YOU WILL NEED THE MECHSHAKER MOD FOR THIS TO WORK - https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029


Setup (NEW - MAKE SURE TO READ):

Download and set up MechShaker - https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029 - MechShakerRelay MUST be active. MechShaker app itself does not need to run for MechFFB to work, but if you have bass shakers this is non-negotiable really - put them to work and go turn MechShaker on.

Download latest MechFFB release, extract anywhere you like

NEW STEP: Replace MechShakerRelay.pak in the MW5 mods folder loacted in MechShakerRelay>Paks with the MechShakerRelay.pak included in the latest release .zip file.

Run MechFFB.exe

Select your FFB Joystick (do not select your virtual controller)

Click "Test Device" to verify FFB output is actually working. (This seems to be finicky on some devices, don't worry if it doesn't work)

Click "Start Engine"

Launch MW5 and ensure MechShaker mod is enabled

Enable "Invert Axis" to invert force feedback (only if your device requires this - weapon fire should pull towards you, not away from you - enable invert axis if weapon fire pulls away from you) - Only confirmed working on Moza AB9 for version 3.0.0.




Features

DirectInput backend (confirmed working on MOZA AB9, VPForce Rhino, Microsoft Sidewinder, yet to test other devices)

Weapon recoil effects

Damage impact effects, now with direction

Simple intensity controls

Advanced per-weapon tuning

Movement/footstep effects

Destruction effects (when walking over mechs going critical, explosive infrastructure or stomping on tanks)

Settings autosave after adjustments




Quirks/issues:

The "Events" count on the right will always read 0 regardless of whether events are successfully being read - refer to the debug window instead

In similar fashion, this text will still say "Waiting for Mechwarrior 5..." even after it has already successfully connected

YAML and YAW introduce different firing modes for missiles. Streak LRMs can be fired as individual missiles and the FFB is mapped appropriately, but normal LRMs firing in salvos does not register separate events at this stage, so you will only get one 'missile firing' event.





How it works (foundation built upon MechShaker)

MechShakerRelay, a blueprint mod for the game, gathers on-tick data and listens for a variety of in game events, packages up necessary details, and calls a parameterised OnTelemetry event that performs no immediate actions

MechShakerBridge, a C++ plugin, is injected into the game using one of two methods.

If using MechWarriorVR, as a UEVR plugin - https://github.com/sicsix/MW5-UEVR-Plugins

Otherwise, as a plugin for UnrealModLoader

MechShakerBridge, when using the UEVR plugin, hooks directly into the OnTelemetry blueprint event. It then writes this telemetry data out to a memory mapped file.

MechFFB reads this memory mapped file and converts in game events into force feedback output
