namespace DeadWare.Core;
public record HardwareSources(bool Cpu,bool Gpu,bool Memory,bool Motherboard,bool Controllers,bool Storage);
public record CollectorOptions(bool HardwareEnabled=true,bool EtwEnabled=true,bool ExtendedSensors=false,bool DiagnosticTimings=false,double PollingIntervalSeconds=1)
{
    public HardwareSources Sources=>new(HardwareEnabled,HardwareEnabled,HardwareEnabled&&ExtendedSensors,HardwareEnabled&&ExtendedSensors,HardwareEnabled&&ExtendedSensors,HardwareEnabled&&ExtendedSensors);
}
public static class PollingPolicy
{
    public static IReadOnlyList<double> Intervals {get;}=Array.AsReadOnly(new[]{0.5,1,2,5,10});
    public static double Normalize(double intervalSeconds)=>intervalSeconds is .5 or 1 or 2 or 5 or 10?intervalSeconds:1;
    public static double FreshnessSeconds(double intervalSeconds)=>Math.Max(4,Normalize(intervalSeconds)+3);
    public static TimeSpan DelayAfter(double elapsedSeconds,double intervalSeconds=1)
    {
        double interval=Normalize(intervalSeconds);
        return TimeSpan.FromSeconds(!double.IsFinite(elapsedSeconds)||elapsedSeconds>=interval?interval:Math.Max(0.25,interval-Math.Max(0,elapsedSeconds)));
    }
}
