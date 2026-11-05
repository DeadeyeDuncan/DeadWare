using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Windows' aggregate system battery, including battery-saver and Energy saver status. No driver or elevation.</summary>
public static class WindowsBattery
{
    public static BatteryReading Read()
    {
        EnergySaverStatus.EnsureStarted();
        try
        {
            return GetSystemPowerStatus(out var state)
                ? BatteryReading.FromWindows(state.Ac, state.Flags, state.Percentage, state.Saver, EnergySaverStatus.Current)
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

/// <summary>Windows 11 24H2+ reports Energy saver's standard level only through the GUID_ENERGY_SAVER_STATUS power setting; the
/// legacy SystemStatusFlag stays 0. One process-wide callback registration (no elevation) caches the latest level for every poll.
/// Until Windows reports a level, as on older Windows, <see cref="Current"/> is null and the legacy flag decides alone.</summary>
internal static class EnergySaverStatus
{
    private const uint DeviceNotifyCallback = 2, PowerSettingChange = 0x8013;
    private static readonly object gate = new();
    // The native registration holds a pointer to this delegate for the life of the process, so it stays rooted here.
    private static readonly PowerSettingCallback callback = OnChange;
    // Set by the first reported level. Windows delivers the current level on a system thread just after registering.
    private static readonly ManualResetEventSlim reported = new(false);
    private static IntPtr parameters, registration;
    private static int level = -1, attempts;
    private static bool attempted;

    /// <summary>The latest reported level (0 off, 1 standard, 2 high savings), or null before Windows reports one.</summary>
    public static int? Current => Volatile.Read(ref level) is >= 0 and var value ? value : null;

    /// <summary>Registration attempts this process has made: one after the first battery read, never more.</summary>
    internal static int Attempts => Volatile.Read(ref attempts);

    /// <summary>Registers once per process. Windows calls back with the current level just after registering, then on every
    /// change. A failed registration leaves <see cref="Current"/> null and is not retried.</summary>
    public static void EnsureStarted()
    {
        if (Volatile.Read(ref attempted)) return;
        lock (gate)
        {
            if (attempted) return;
            Volatile.Write(ref attempts, attempts + 1);
            try
            {
                // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback, Context }, kept for the life of the registration.
                parameters = Marshal.AllocHGlobal(2 * IntPtr.Size);
                Marshal.WriteIntPtr(parameters, Marshal.GetFunctionPointerForDelegate(callback));
                Marshal.WriteIntPtr(parameters, IntPtr.Size, IntPtr.Zero);
                var setting = EnergySaver.StatusSetting;
                if (PowerSettingRegisterNotification(ref setting, DeviceNotifyCallback, parameters, out var handle) == 0)
                {
                    registration = handle;
                    // The first level arrives on another thread after the call returns. Wait for it once, briefly, so the first
                    // reading is not decided by the legacy flag alone (which would record a false "off" at every launch).
                    // Windows without Energy saver never reports, and pays this once. The callback never takes the gate.
                    reported.Wait(TimeSpan.FromMilliseconds(250));
                }
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
            finally
            {
                if (registration == IntPtr.Zero && parameters != IntPtr.Zero) { Marshal.FreeHGlobal(parameters); parameters = IntPtr.Zero; }
                Volatile.Write(ref attempted, true);
            }
        }
    }

    private static uint OnChange(IntPtr context, uint type, IntPtr setting)
    {
        // Runs on a system thread: an exception must never unwind into the native caller.
        try { if (ReadSetting(type, setting) is { } value) { Volatile.Write(ref level, value); reported.Set(); } }
        catch (Exception) { }
        return 0;
    }

    /// <summary>The Energy saver level carried by a PBT_POWERSETTINGCHANGE callback's POWERBROADCAST_SETTING, or null.</summary>
    internal static int? ReadSetting(uint type, IntPtr setting)
    {
        if (type != PowerSettingChange || setting == IntPtr.Zero) return null;
        // GUID (16 bytes), DWORD DataLength, then DataLength bytes of data: copy past offset 20 only when a DWORD is there.
        if (Marshal.ReadInt32(setting, 16) < 4) return null;
        var bytes = new byte[24];
        Marshal.Copy(setting, bytes, 0, bytes.Length);
        return EnergySaver.Parse(bytes);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerSettingCallback(IntPtr context, uint type, IntPtr setting);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, IntPtr recipient, out IntPtr registrationHandle);
}
