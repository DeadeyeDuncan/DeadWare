namespace DeadWare.Core;
/// <summary>Build-time switches for the Processes feature, set from the CI measurements in docs/validation.md (Processes 0.13.0).</summary>
public static class ProcessFeatures
{
    public static bool DiskColumns {get;}=true;
    public static bool ScanLoop {get;}=false;
    public static int DiskBufferMultiplier {get;}=1;
}
