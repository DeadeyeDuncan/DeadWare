namespace DeadWare.Devices;

// Lighting 5: what OpenRGB's hub detector is to be at the next launch, and what the launch in progress (or the last one) wrote. Shared by
// Program's prepare callback and the host. A launch reads On once, so the host knows which way every OpenRGB that runs was started.
internal sealed class HubDetector
{
    volatile bool on=true,launchedOn=true;
    public bool On{get=>on;set=>on=value;}
    public bool LaunchedOn=>launchedOn;
    public bool Launching()
    {
        var value=on;
        launchedOn=value;
        return value;
    }
}
