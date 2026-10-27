using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Windows' aggregate system battery, including battery-saver status. No driver or elevation.</summary>
public static class WindowsBattery
{
    public static BatteryReading Read()
    {
        try
        {
            return GetSystemPowerStatus(out var state)
                ? BatteryReading.FromWindows(state.Ac, state.Flags, state.Percentage, state.Saver)
                : BatteryReading.Unavailable;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { return BatteryReading.Unavailable; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus state);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte Ac, Flags, Percentage, Saver;
        public uint LifeSeconds, FullLifeSeconds;
    }
}
