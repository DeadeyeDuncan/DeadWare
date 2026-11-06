namespace DeadWare.Core;
/// <summary>Build-time switches for the Processes feature; later tasks set the values.</summary>
public static class ProcessFeatures
{
    public static bool DiskColumns {get;}=false;
    public static bool ScanLoop {get;}=false;
    public static int DiskBufferMultiplier {get;}=1;
}
