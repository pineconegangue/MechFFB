SDL2 SETUP REQUIRED
===================

This project now uses SDL2 for force feedback instead of SharpDX DirectInput.

IMPORTANT: You need to download SDL2.dll and place it in the output directory.

Steps:
1. Download SDL2-2.x.x-win32-x64.zip from https://github.com/libsdl-org/SDL/releases
2. Extract SDL2.dll from the zip file
3. Copy SDL2.dll to: MechFFBUI/bin/Debug/net8.0-windows/SDL2.dll
   (Or wherever your build output directory is)

Alternative: The NuGet package SDL2-CS.Core includes the DLL automatically.
Let's add that package instead!
