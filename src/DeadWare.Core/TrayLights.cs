namespace DeadWare.Core;
// The tray menu's lights line: its label, whether it can be clicked, and the switch value a click sets.
public static class TrayLights
{
    public const string Off="Turn lights off",On="Turn lights on",Unavailable="Lights (not available)";
    // The label follows the saved switch, not the effective value, so a click always flips what the owner chose. Greyed only with lighting
    // disabled, no helper state (not connected), the helper starting or a client problem (the pipe refused); whatever else OpenRGB is doing, the
    // helper takes the switch, as the page's does (fix wave 1, F6).
    public static (string Label,bool Enabled,bool Target) Line(bool lightingEnabled,HostState? state,string? clientProblem)
    {
        if(!lightingEnabled||state is null||clientProblem is not null||state.Status==HostStatus.Starting)return(Unavailable,false,false);
        return state.Settings.LightsOn?(Off,true,false):(On,true,true);
    }
}
