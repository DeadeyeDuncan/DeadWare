namespace DeadWare.Core;

/// <summary>
/// An elevated lighting host's refusal to run OpenRGB from its folder (Ruling AM), in words that fit the reason (polish item 8): a folder
/// that programs without administrator rights can change gets the writable text, and one the check couldn't judge (missing, unreadable,
/// on a network drive, or a link or junction) the can't-check text with the reason. Settings recognises both, so its own line about the
/// folder gives way to the host's and the refusal shows once.
/// </summary>
public static class OpenRgbFolderRefusal
{
    public const string WritableText=@"The OpenRGB folder can be changed by programs without administrator rights, so DeadWare won't run it as administrator. Copy OpenRGB to C:\Program Files\OpenRGB and choose it in Settings → Lighting.";
    const string CantCheckStart="DeadWare can't check the OpenRGB folder, so it won't run OpenRGB as administrator: ";
    const string Remedy=@" Copy OpenRGB to C:\Program Files\OpenRGB and choose it in Settings → Lighting.";

    /// <summary>The refusal for InstallLocation's reason.</summary>
    public static string Text(string? reason)=>CantCheck(reason)?CantCheckText(reason):WritableText;

    /// <summary>The can't-check refusal, giving the reason the folder couldn't be judged.</summary>
    public static string CantCheckText(string? reason)=>CantCheckStart+(reason is {Length:>0}?reason:"its permissions couldn't be read.")+Remedy;

    /// <summary>
    /// InstallLocation.Evaluate's reasons for a folder it couldn't judge: a missing or unreadable entry, one on a network drive, a link or
    /// junction, or nothing checked. Its other reasons, an owner or a rule that lets other programs change the folder, establish that it
    /// can be changed.
    /// </summary>
    public static bool CantCheck(string? reason)=>reason is null||reason=="No folder permissions were checked."
        ||reason.StartsWith("Permissions for ",StringComparison.Ordinal)&&reason.Contains(" could not be read: ",StringComparison.Ordinal)
        ||reason.EndsWith(" is not on a local drive.",StringComparison.Ordinal)||reason.EndsWith(" is a link or junction.",StringComparison.Ordinal);

    /// <summary>The host's status message reports a folder refusal; a lighting.json note may follow it.</summary>
    public static bool Is(string? message)=>message is not null&&(message.StartsWith(WritableText,StringComparison.Ordinal)||message.StartsWith(CantCheckStart,StringComparison.Ordinal));
}
