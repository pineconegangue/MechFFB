# SDL2 Setup Instructions

## You Need SDL2.dll!

The SDL2-CS NuGet package only includes C# bindings, not the native SDL2.dll library.
You need to download it manually.

## Quick Setup (2 minutes):

### Step 1: Download SDL2
Go to: https://github.com/libsdl-org/SDL/releases/latest

Look for: **SDL2-2.x.x-win32-x64.zip** (or similar)

Download and extract it.

### Step 2: Copy SDL2.dll
From the extracted zip, find **SDL2.dll** and copy it to:

```
MechFFB/SDL2.dll
```

(That's the root folder of the solution, next to MechFFB.sln)

### Step 3: Build
Build the solution. The build process will automatically copy SDL2.dll to the output directory.

## Alternative: Manual Copy
If the auto-copy doesn't work, manually copy SDL2.dll to:
```
MechFFB/MechFFBUI/bin/Debug/net8.0-windows/SDL2.dll
```

## Verification
After building, check that SDL2.dll exists in the same folder as MechFFB.exe

## Download Link
Direct link to SDL2 releases: https://github.com/libsdl-org/SDL/releases

You want the **Windows x64** runtime binaries (not development files).

## Notes
- SDL2-CS version 2.0.0 is compatible with SDL2 version 2.0.x and 2.24+
- Make sure you get the 64-bit (x64) version
- You only need SDL2.dll, not the other files in the archive
