namespace DeadWare.Core;
// What Settings → Lighting's hub switch shows and waits for, as small rules over the helper's state. HubSwitchFlow strings them into the sequence.
public static class HubFlow
{
    public static readonly TimeSpan ReadWait=TimeSpan.FromSeconds(3),BackWait=TimeSpan.FromSeconds(60);
    // The dialog's reading once it is newer than the click that asked for it; null before then, and with no reading.
    public static HubReading? Fresh(HubView? hub,DateTimeOffset clicked)=>hub?.Read is {} read&&read.At>clicked?read:null;
    // The hub is off and OpenRGB lists it again: its lights are back and a take-over can be read.
    public static bool BackThroughOpenRgb(HostState? state)=>state?.Hub?.State==HubStates.Off&&(state.Devices??[]).Any(d=>d.Hub==HubRoutes.OpenRgb);
    // What the switch shows: on while DeadWare drives the hub or has let go for now, off when it is off or was given back, and while switching as it will be.
    public static bool SwitchOn(HubView? hub,bool turningOn)=>hub?.State switch
    {
        HubStates.Driving or HubStates.LetGo=>true,
        HubStates.Switching=>turningOn,
        _=>false,
    };
    // After the hub was given back, turning the switch on does what "Try the take-over again" does: a read there would be refused as still switching.
    public static bool RetryFirst(HubView? hub)=>hub?.State==HubStates.GaveBack;
}
