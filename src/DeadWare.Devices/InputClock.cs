using System.Runtime.InteropServices;

namespace DeadWare.Devices;

// Lighting 4, the input rule: idle time is GetLastInputInfo only and mouse use is GetCursorPos only, both named in this file alone. Neither
// says anything about which key or button was used; the position is compared, never logged.
// The Windows functions this file may import are exactly: GetLastInputInfo, GetTickCount, GetCursorPos (KeyPressRuleTests holds the list).
internal sealed class InputClock:IInputClock
{
    int lastX,lastY;

    // Milliseconds since the last input. Both times are 32-bit tick counts, so unsigned subtraction makes the 49-day wrap harmless; the
    // tick is read after the input time, so it is never the earlier of the two.
    public long IdleMs()
    {
        var info=new LastInput{Size=(uint)Marshal.SizeOf<LastInput>()};
        if(!GetLastInputInfo(ref info)) return 0;
        return unchecked(GetTickCount()-info.Time);
    }

    // The pointer's screen position; when Windows can't say (a secure desktop), the last position read, so a failed read never looks like a move.
    public (int X,int Y) Pointer()
    {
        if(GetCursorPos(out var p)){lastX=p.X;lastY=p.Y;}
        return (lastX,lastY);
    }

    [StructLayout(LayoutKind.Sequential)] struct LastInput{public uint Size;public uint Time;}
    [StructLayout(LayoutKind.Sequential)] struct Point{public int X,Y;}
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out Point point);
}
