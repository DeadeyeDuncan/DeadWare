namespace DeadWare.Core;
public record HardwareSources(bool Cpu,bool Gpu,bool Memory,bool Motherboard,bool Controllers,bool Storage);
public record CollectorOptions(bool HardwareEnabled=true,bool EtwEnabled=true,bool ExtendedSensors=false,bool DiagnosticTimings=false)
{
    public HardwareSources Sources=>new(HardwareEnabled,HardwareEnabled,HardwareEnabled&&ExtendedSensors,HardwareEnabled&&ExtendedSensors,HardwareEnabled&&ExtendedSensors,HardwareEnabled&&ExtendedSensors);
}
public static class PollingPolicy
{
    public static TimeSpan DelayAfter(double elapsedSeconds)=>TimeSpan.FromSeconds(!double.IsFinite(elapsedSeconds)||elapsedSeconds>=1?1:Math.Max(0.25,1-Math.Max(0,elapsedSeconds)));
}
