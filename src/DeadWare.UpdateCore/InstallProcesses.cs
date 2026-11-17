namespace DeadWare.UpdateCore;

public static class InstallProcesses
{
    public const string DevicesExecutable="DeadWare.Devices.exe";
    public static IReadOnlyList<int> Running(string installDirectory,IEnumerable<(int Id,string? Path)> processes)
    {
        string host=Path.Combine(installDirectory,DevicesExecutable);
        return processes.Where(p=>!string.IsNullOrEmpty(p.Path)&&string.Equals(p.Path,host,StringComparison.OrdinalIgnoreCase)).Select(p=>p.Id).ToList();
    }
}
