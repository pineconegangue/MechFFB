# MechFFB Quick Start Guide

Get force feedback working in MechWarrior 5 in 5 minutes!

---

## Prerequisites Checklist

Before starting, make sure you have:

- ✅ **MechWarrior 5: Mercenaries** installed (PC version)
- ✅ **Force feedback joystick** connected and working in Windows
- ✅ **MechShakerRelay mod** installed ([Get it here](https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029))
- ✅ **MechShakerBridge plugin** installed (UEVR or UML version)
- ✅ **.NET 8.0 Runtime** installed ([Download here](https://dotnet.microsoft.com/download/dotnet/8.0))

**Don't have MechShakerRelay/Bridge?** These are required for MechFFB to work. They're the same mods used by MechShaker.

---

## Step-by-Step Setup

### 1️⃣ Download MechFFB
1. Go to [Releases](https://github.com/yourusername/MechFFB/releases)
2. Download the latest `MechFFB-v0.x.x.zip`
3. Extract to a folder (e.g., `C:\MechFFB\`)

### 2️⃣ First Launch
1. Run `MechFFB.exe`
2. You should see the main window with a device dropdown

![MechFFB Main Window](docs/screenshot-main.png)

### 3️⃣ Select Your Device
1. Click the **"Refresh Devices"** button
2. Your force feedback joystick should appear in the dropdown
3. Select it from the list

**No devices showing?**
- Make sure your joystick is plugged in
- Check Windows Device Manager → Human Interface Devices
- Try unplugging and replugging the joystick
- Click "Refresh Devices" again

### 4️⃣ Test Your Device
1. Click **"Test Device"** button
2. You should feel a brief recoil effect
3. If you don't feel anything, check:
   - Joystick is powered on (if it has power)
   - Force feedback is enabled in joystick software
   - Windows has control panel → Devices → check "Enable force feedback"

### 5️⃣ Adjust Settings (Optional)
Before starting, you can adjust the intensity sliders in the **Simple Mode** tab:

- **Master Intensity**: Overall strength (start at 50-70%)
- **Weapon Recoil**: How much weapons kick
- **Incoming Damage**: How hard hits feel
- **Movement**: Footsteps intensity
- **Jump Jets**: Thrust feedback
- **Impacts**: Landing/collision strength

**Tip**: Start with lower intensities and work up. Effects can be quite strong!

### 6️⃣ Start the Engine
1. Click the big green **"START"** button
2. Status should change to "Waiting for MechWarrior 5..."
3. The telemetry indicator will be red (not connected yet)

### 7️⃣ Launch MechWarrior 5
1. Start MechWarrior 5: Mercenaries
2. Load into a mission or instant action
3. Watch MechFFB - the telemetry indicator should turn **GREEN** ✅
4. Status will change to "Connected to MW5"

### 8️⃣ Start Playing!
That's it! Now when you play:

- **Fire weapons** → Feel recoil kick your stick
- **Take damage** → Feel hits from the direction they came from
- **Walk around** → Feel footsteps (tonnage-based intensity)
- **Use jump jets** → Feel upward thrust
- **Land hard** → Feel impact jarring

---

## Recommended Initial Settings

For first-time users, try these settings:

| Setting | Value | Why |
|---------|-------|-----|
| Master Intensity | 60% | Safe starting point |
| Weapon Recoil | 70% | Noticeable but not overwhelming |
| Incoming Damage | 80% | You want to FEEL hits |
| Movement | 50% | Can get annoying if too high |
| Jump Jets | 60% | Moderate thrust feel |
| Impacts | 100% | Landing should feel impactful |

**After a few missions**, adjust to taste. Some people prefer subtle, others want MAX POWER.

---

## Troubleshooting

### "Not Connected to MW5" (Red indicator)

**Possible causes:**
1. MechShakerRelay mod not enabled in game
2. MechShakerBridge plugin not loaded
3. Game not actually in a mission (just in menus)

**Solutions:**
- Check mod list in MW5 - MechShakerRelay should be enabled
- Restart the game
- Try loading into instant action

### Effects feel weak

**Try:**
- Increase Master Intensity slider
- Increase individual effect sliders
- Check if your joystick has a physical FFB strength knob
- Some joysticks need driver software for full strength

### Effects feel too strong

**Try:**
- Reduce Master Intensity to 40-50%
- Reduce Weapon Recoil specifically
- Check joystick physical FFB strength settings

### Game crashes when MechFFB is running

**Unlikely, but if it happens:**
- Stop MechFFB
- Restart game
- Contact us with crash log

### Device disconnects during gameplay

**Usually a USB issue:**
- Use a different USB port (try USB 2.0)
- Check USB cable
- Update joystick drivers

---

## Tips for Best Experience

✨ **Combine with MechShaker**: Run both for bass shaker + FFB = ultimate immersion

🎮 **Adjust per weapon**: In Advanced Mode (coming soon), tune AC/20s differently than lasers

⚡ **Lower is often better**: Start with moderate settings, subtle can be more immersive than intense

🔊 **VR Users**: Effects work great in VR with the UEVR plugin

🎯 **Practice mode**: Use instant action to test settings before campaign missions

---

## Advanced: Running with MechShaker

MechFFB and MechShaker can run simultaneously for the ultimate experience:

1. Start **MechShaker** first
2. Start **MechFFB** second
3. Both will read from the same telemetry stream
4. Now you have:
   - Bass shaker rumbling your seat
   - Joystick kicking with recoil
   - Full sensory immersion! 🚀

---

## Need Help?

- 📖 [Full README](README.md)
- 🛠️ [Developer Guide](DEVELOPER.md)
- 🐛 [Report Issues](https://github.com/yourusername/MechFFB/issues)
- 💬 [Discussions](https://github.com/yourusername/MechFFB/discussions)

---

## Next Steps

Once you're comfortable with MechFFB:

1. Experiment with different intensity settings
2. Try different weapons in-game and feel the difference
3. Join the community and share your settings
4. Help test new features in beta builds

**Enjoy your immersive MechWarrior 5 experience!** 🤖⚔️
