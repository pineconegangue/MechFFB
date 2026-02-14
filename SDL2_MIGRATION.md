# MechFFB - SDL2 Migration

## What Changed

This version has been migrated from **SharpDX DirectInput** to **SDL2** for force feedback.

### Why SDL2?

- **Better Compatibility**: SDL2 is what DCS and many modern games use for FFB
- **Active Development**: SDL2 is actively maintained, SharpDX is deprecated
- **Proven Track Record**: If DCS works with your MOZA AB9, SDL2 should work too

### What You Need to Know

1. **First Build**: When you first build the project, NuGet will automatically download:
   - SDL2-CS (C# wrapper)
   - SDL2-CS.Core (includes SDL2.dll native library)

2. **Testing**: After building:
   - Run MechFFBUI.exe
   - Your MOZA AB9 should appear in the device dropdown
   - Click "Test Device" - you should feel a force feedback effect!

3. **If It Doesn't Work**:
   - Check the console output (running from Visual Studio shows this)
   - SDL2 provides much better error messages than DirectInput
   - Common issues:
     - SDL2.dll not in output directory (should be automatic with SDL2-CS.Core package)
     - Device not properly connected
     - Device already in use by another application

### API Changes

The FFB engine API is slightly different:

**Old (DirectInput)**:
```csharp
var devices = engine.GetAvailableDevices(); // Returns List<DeviceInstance>
engine.SelectDevice(deviceGuid);
```

**New (SDL2)**:
```csharp
var devices = engine.GetAvailableDevices(); // Returns List<SDL2HapticManager.HapticDeviceInfo>
engine.SelectDevice(deviceIndex); // Use index instead of GUID
```

### Next Steps

If this works, we can:
1. Re-implement all the sophisticated effect types (recoil, damage, movement, etc.)
2. Add more effect types that SDL2 supports (sine waves, ramps, etc.)
3. Improve the effect scheduling and blending

### Troubleshooting

**"SDL_Init failed" error**:
- Make sure SDL2.dll is in the same directory as MechFFBUI.exe
- Try running as Administrator
- Check if another program has exclusive control of your device

**No devices found**:
- Disconnect and reconnect your FFB device
- Try a different USB port
- Check Windows Device Manager

**Device selection fails**:
- Close DCS or other programs using the device
- Restart the application
- Try selecting the device again

### Reverting to DirectInput

If you need to revert:
1. Replace SDL2-CS packages with SharpDX.DirectInput in .csproj
2. Restore the old DirectInputManager.cs
3. Update FFBEngine.cs to use DirectInputManager

But hopefully SDL2 works better! 🤞
