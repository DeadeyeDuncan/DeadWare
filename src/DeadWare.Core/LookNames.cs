namespace DeadWare.Core;

// Lighting 3b: the window's checks on a saved look's name before it asks the host, in the host's terms (trimmed, 1 to 40 characters,
// compared ignoring case), and whether choosing a look would lose a setup that is in no look.
public static class LookNames
{
    // renaming: the look's current name, which the new one may match in any case.
    public static string? Problem(string? name,IReadOnlyList<string> existing,string? renaming=null)
    {
        var n=name?.Trim()??"";
        if(n.Length==0) return "Give the look a name.";
        if(n.Length>DeviceLayers.MaxLookName) return $"A look's name can be up to {DeviceLayers.MaxLookName} characters.";
        bool Same(string? a)=>string.Equals(a?.Trim(),n,StringComparison.OrdinalIgnoreCase);
        if(renaming is not null&&Same(renaming)) return null;
        return existing.Any(Same)?$"There is already a look called {n}.":null;
    }
    // The keyboard has changed since its look was chosen or saved, or it has paint, layers or a lighting mode and no look. pending: a
    // change the window asked for (painted keys, layers) isn't in the state d yet, so the state can't say it is saved.
    public static bool Unsaved(DeviceView d,bool pending=false)=>pending||d.LookChanged||d.Look is null&&(d.Paint is{Count:>0}||d.Layers is{Count:>0}||d.LightingMode is not null);
}
