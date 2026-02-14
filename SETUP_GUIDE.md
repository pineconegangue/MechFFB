# MechFFB Setup Guide - vJoy + Joystick Gremlin

## How It Works (Same as DCS)

MechFFB uses the same approach as DCS World:

- **Input**: MechWarrior 5 reads from vJoy (combined inputs via Joystick Gremlin)
- **Force Feedback**: MechFFB sends directly to your MOZA AB9 (bypassing vJoy)

This is exactly how DCS handles it - vJoy doesn't relay FFB, the game talks to the physical device directly.

## Setup Steps

### 1. Configure MechFFB

1. **Launch MechFFB**
2. **Backend**: Use SDL2 (default, works great with MOZA AB9)
3. **Select Device**: Choose **"MOZA AB9 FFB Base"** (NOT vJoy)
4. **Test**: Click "Test Device" - your stick should move!

### 2. Configure MechWarrior 5

1. **Open MW5 Controls**
2. **Joystick**: Select **vJoy Device** (for your combined inputs)
3. **Map all your controls** to vJoy axes/buttons (as configured in Joystick Gremlin)

### 3. Configure Joystick Gremlin

- Keep your existing profile that combines devices onto vJoy
- No FFB configuration needed in Gremlin - MechFFB handles that directly

### 4. Play!

1. **Start Joystick Gremlin** (if not auto-starting)
2. **Start MechFFB** and click "Start Engine"
3. **Launch MechWarrior 5**
4. Feel the force feedback from weapon fire, damage, movement!

## Why This Works

**vJoy Device 1**:
- Combines your physical controls (pedals, throttle, etc.)
- MW5 reads input from here
- Does NOT relay force feedback

**MOZA AB9**:
- MechFFB sends force feedback directly here
- Works perfectly with SDL2
- No vJoy in the middle

This is the professional sim setup - same as DCS, IL-2, etc.

## Troubleshooting

**"No force feedback in game"**
- Make sure you selected MOZA AB9 in MechFFB (not vJoy)
- Check that MechFFB status shows "FFB Active"
- Verify MechShaker mod is installed in MW5

**"Controls not working in MW5"**
- Make sure Joystick Gremlin is running
- Verify MW5 is set to use vJoy for controls
- Check vJoy Device 1 is enabled in vJoy Configure

**"Test works but no FFB in game"**
- Check MechShaker mod is properly installed
- Verify MW5 is actually running and generating telemetry
- Look at MechFFB console for telemetry connection status

## Next Steps

Once basic FFB is working:
1. Tune intensity sliders in MechFFB
2. Experiment with different effect strengths
3. Enable/disable specific effect types
4. Consider Advanced Mode for per-weapon tuning

Enjoy your force feedback! 🎮
