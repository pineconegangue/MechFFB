using System.Runtime.InteropServices;

namespace MechFFBEngine.Haptic;

/// <summary>
/// Minimal SDL2 P/Invoke declarations for haptic (force feedback) support
/// No external dependencies - just requires SDL2.dll
/// </summary>
public static class SDL2
{
    private const string nativeLibName = "SDL2.dll";

    // SDL Init flags
    public const uint SDL_INIT_JOYSTICK = 0x00000200;
    public const uint SDL_INIT_HAPTIC = 0x00001000;

    // Haptic effect types
    public const ushort SDL_HAPTIC_CONSTANT = (1 << 0);
    public const ushort SDL_HAPTIC_SINE = (1 << 1);
    public const ushort SDL_HAPTIC_LEFTRIGHT = (1 << 2);
    public const ushort SDL_HAPTIC_TRIANGLE = (1 << 3);
    public const ushort SDL_HAPTIC_SAWTOOTHUP = (1 << 4);
    public const ushort SDL_HAPTIC_SAWTOOTHDOWN = (1 << 5);
    public const ushort SDL_HAPTIC_RAMP = (1 << 6);
    public const ushort SDL_HAPTIC_SPRING = (1 << 7);
    public const ushort SDL_HAPTIC_DAMPER = (1 << 8);
    public const ushort SDL_HAPTIC_INERTIA = (1 << 9);
    public const ushort SDL_HAPTIC_FRICTION = (1 << 10);
    public const ushort SDL_HAPTIC_CUSTOM = (1 << 11);

    // Direction types
    public const byte SDL_HAPTIC_CARTESIAN = 1;

    // Core SDL functions
    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_Init(uint flags);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Quit();

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GetError();

    public static string SDL_GetErrorString()
    {
        IntPtr ptr = SDL_GetError();
        return Marshal.PtrToStringAnsi(ptr) ?? "Unknown error";
    }

    // Joystick functions
    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_NumJoysticks();

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_JoystickOpen(int device_index);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_JoystickClose(IntPtr joystick);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_JoystickNameForIndex(int device_index);

    public static string SDL_JoystickNameForIndexString(int device_index)
    {
        IntPtr ptr = SDL_JoystickNameForIndex(device_index);
        return Marshal.PtrToStringAnsi(ptr) ?? $"Device {device_index}";
    }

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickIsHaptic(IntPtr joystick);

    // Haptic functions
    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_HapticOpenFromJoystick(IntPtr joystick);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_HapticClose(IntPtr haptic);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint SDL_HapticQuery(IntPtr haptic);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticNumAxes(IntPtr haptic);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticNumEffects(IntPtr haptic);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticNumEffectsPlaying(IntPtr haptic);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticNewEffect(IntPtr haptic, ref SDL_HapticEffect effect);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticRunEffect(IntPtr haptic, int effect, uint iterations);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticStopEffect(IntPtr haptic, int effect);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_HapticDestroyEffect(IntPtr haptic, int effect);

    [DllImport(nativeLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_HapticStopAll(IntPtr haptic);

    // Structures
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_HapticDirection
    {
        public byte type;
        public int dir0;
        public int dir1;
        public int dir2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_HapticConstant
    {
        public ushort type;
        public SDL_HapticDirection direction;
        public uint length;
        public ushort delay;
        public ushort button;
        public ushort interval;
        public short level;
        public ushort attack_length;
        public ushort attack_level;
        public ushort fade_length;
        public ushort fade_level;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_HapticPeriodic
    {
        public ushort type;
        public SDL_HapticDirection direction;
        public uint length;
        public ushort delay;
        public ushort button;
        public ushort interval;
        public ushort period;
        public short magnitude;
        public short offset;
        public ushort phase;
        public ushort attack_length;
        public ushort attack_level;
        public ushort fade_length;
        public ushort fade_level;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct SDL_HapticEffect
    {
        [FieldOffset(0)]
        public ushort type;

        [FieldOffset(0)]
        public SDL_HapticConstant constant;
        
        [FieldOffset(0)]
        public SDL_HapticPeriodic periodic;
    }
}
