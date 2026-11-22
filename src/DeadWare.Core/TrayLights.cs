namespace DeadWare.Core;
// The tray menu's lights line: its label, whether it can be clicked, and the switch value a click sets.
public static class TrayLights
{
    public const string Off="Turn lights off",On="Turn lights on",Unavailable="Lights (not available)";
    // The label follows the saved switch, not the effective value, so a click always flips what the owner chose.
    public static (string Label,bool Enabled,bool Target) Line(bool lightingEnabled,HostState? state,string? clientProblem)
    {
        if(!lightingEnabled||state is null||clientProblem is not null||state.Status!=HostStatus.Running)return(Unavailable,false,false);
        return state.Settings.LightsOn?(Off,true,false):(On,true,true);
    }
}
