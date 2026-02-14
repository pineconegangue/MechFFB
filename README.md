# MechFFB - Force Feedback for MechWarrior 5

Force feedback system for MechWarrior 5 using SDL2 (same as DCS World).

## Quick Start

### If Using vJoy + Joystick Gremlin:

**In MechFFB**: Select **MOZA AB9 FFB Base** (NOT vJoy)  
**In MW5**: Use **vJoy** for controls

This is exactly how DCS works - vJoy for input, physical device for FFB.

See **SETUP_GUIDE.md** for detailed instructions.

### Direct Setup (No vJoy):

1. Build and run MechFFBUI
2. Select your MOZA AB9
3. Click "Test Device" - feel the force!
4. Click "Start Engine"
5. Launch MW5 and play

## Requirements

- **SDL2.dll** - Run `Download-SDL2.ps1` or download manually
- **MechWarrior 5** with **MechShaker mod** installed
- **Force feedback device** (MOZA AB9 tested and working)

## Features

- ✅ SDL2 backend (same as DCS)
- ✅ DirectInput backend (alternative)
- ✅ Weapon recoil effects
- ✅ Damage impact effects
- ✅ Simple intensity controls
- 🚧 Advanced per-weapon tuning (coming soon)
- 🚧 Movement/footstep effects (coming soon)

## Why Two Backends?

**SDL2** (Default):
- Same library DCS uses
- Direct device access
- Best for MOZA AB9

**DirectInput**:
- Alternative if SDL2 has issues
- Legacy compatibility

Both work with vJoy setups when you select the physical device!

## Next Steps

If the test works:
1. Install MechShaker mod in MW5
2. Configure MW5 to use vJoy (if using Joystick Gremlin)
3. Start MechFFB and click "Start Engine"  
4. Launch MW5 and feel the effects!

See **SETUP_GUIDE.md** for complete setup instructions.
