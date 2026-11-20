namespace DeadWare.Core;

/// <summary>What InstallLocation.Evaluate's reason says is wrong with the path it names.</summary>
public enum LocationFault
{
    /// <summary>"… can be changed by …": an account other than SYSTEM, Administrators and TrustedInstaller may change it.</summary>
    Writable,
    /// <summary>"… is not owned by SYSTEM, Administrators or TrustedInstaller."</summary>
    NotOwned,
    /// <summary>"Permissions for … could not be read: …"</summary>
    Unreadable,
    /// <summary>"… is a link or junction."</summary>
    Link,
    /// <summary>"… is not on a local drive."</summary>
    Remote,
    /// <summary>"No folder permissions were checked.", or no reason at all.</summary>
    NothingChecked,
}

/// <summary>
/// InstallLocation.Evaluate's reasons read back from their words: each is a sentence that names the path at fault, in one of a few fixed
/// forms. The refusals of DeadWare's own folders (polish round 2, item 2) and of the OpenRGB folder both read reasons here, so they agree.
/// </summary>
public static class LocationReason
{
    const string UnreadableStart="Permissions for ",UnreadableMiddle=" could not be read: ",RemoteEnd=" is not on a local drive.",LinkEnd=" is a link or junction.",
        NotOwnedEnd=" is not owned by SYSTEM, Administrators or TrustedInstaller.",WritableMiddle=" can be changed by ",NothingCheckedText="No folder permissions were checked.";

    /// <summary>
    /// The fault and the path the reason names, or null when it names none. A reason in none of InstallLocation's forms counts as one that
    /// lets other programs change the folder, as <see cref="OpenRgbFolderRefusal"/> has always counted it, and names no path.
    /// </summary>
    public static (LocationFault Fault,string? Path) Read(string? reason)
    {
        if(reason is null||reason==NothingCheckedText) return (LocationFault.NothingChecked,null);
        if(reason.StartsWith(UnreadableStart,StringComparison.Ordinal)&&reason.IndexOf(UnreadableMiddle,UnreadableStart.Length,StringComparison.Ordinal) is var end and >=0)
            return (LocationFault.Unreadable,reason[UnreadableStart.Length..end]);
        if(reason.EndsWith(RemoteEnd,StringComparison.Ordinal)) return (LocationFault.Remote,reason[..^RemoteEnd.Length]);
        if(reason.EndsWith(LinkEnd,StringComparison.Ordinal)) return (LocationFault.Link,reason[..^LinkEnd.Length]);
        if(reason.EndsWith(NotOwnedEnd,StringComparison.Ordinal)) return (LocationFault.NotOwned,reason[..^NotOwnedEnd.Length]);
        var by=reason.LastIndexOf(WritableMiddle,StringComparison.Ordinal);
        return (LocationFault.Writable,by>=0?reason[..by]:null);
    }

    /// <summary>The check couldn't judge the path: it is unreadable, a link or junction, not on a local drive, or nothing was checked.</summary>
    public static bool CantCheck(LocationFault fault)=>fault is not (LocationFault.Writable or LocationFault.NotOwned);
}
