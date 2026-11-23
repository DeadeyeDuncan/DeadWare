namespace DeadWare.Core;
// The Aerox 9 mouse card's wording, from the spec's table, as pure functions of the host's MouseView.
public static class MouseTexts
{
    public const string Heading="Mouse · SteelSeries Aerox 9",SharedColours="The same colours are used on the cable and wireless.",
        Battery="Battery",Sleep="Sleep",SleepRow="Mouse sleeps after",Leave="Leave as the mouse is",Never="Never",
        Note="While the mouse lies still its lights hold steady, so it can fall asleep and save battery. Moving it brings the effect back.";
    public static string SubLine(MouseView m)
    {
        var pct=m.Battery is{} b?$" · Battery {b}%":"";
        return m.Connection switch
        {
            MouseConnections.Wireless=>"Wireless"+pct,
            MouseConnections.Cable=>"On its cable"+pct+(m.Charging==true?" · Charging":""),
            MouseConnections.Receiver=>"Receiver plugged in · mouse asleep or out of reach",
            _=>"Not connected"
        };
    }
    // On the receiver with the mouse not answering, a reading is last known whatever its age (fix wave 1, F4).
    public static string BatteryLine(MouseView m)=>m.Battery is{} b&&m.Connection!=MouseConnections.None
        ?m.Stale||m.Connection==MouseConnections.Receiver?$"{b}% · last known":$"{b}%":"Battery unknown";
    public static string Hint(MouseView m)=>m.Connection switch
    {
        MouseConnections.Wireless=>"On battery. Read while you use the mouse.",
        MouseConnections.Cable=>m.Charging==false?"Not charging.":m.Battery==100&&m.Charging==true?"Fully charged.":"Charging. Read every 10 seconds on the cable.",
        MouseConnections.Receiver=>"Last known reading. Move the mouse to refresh it.",
        _=>"Plug in the receiver or the cable."
    };
    public static string SleepChoice(int? minutes)=>minutes switch{null=>Leave,0=>Never,1=>"1 minute",{} n=>n+" minutes"};
    public static string SleepStatus(MouseView m)=>m.SleepMinutes is null?"DeadWare has not changed the mouse's own sleep time."
        :m.SleepSent?"Saved and sent to the mouse."
        :m.Connection==MouseConnections.Cable?"Saved. It is sent over the wireless receiver, so it reaches the mouse next time you use it wireless."
        :"Saved. Waiting for the mouse.";
    public static bool ShowNote(MouseView m)=>m.SleepMinutes>0;
    public static bool Dimmed(MouseView m)=>m.Connection==MouseConnections.None;
    // An absent mouse is drawn unlit in the desk picture.
    public static bool Unlit(DeviceView v)=>v.Mouse?.Connection==MouseConnections.None;
}
