using System.Diagnostics;
using DeadWare.Core;

namespace DeadWare.Devices;

// Lists running processes for LightingConflicts.Find. A protected process refuses MainModule, so its path is just unknown.
internal sealed class ProcessConflictScanner : IConflictScanner
{
    public IReadOnlyList<LightingConflict> Scan(int? ownOpenRgbPid)
    {
        var processes=Process.GetProcesses();
        try
        {
            var list=new List<RunningProcess>(processes.Length);
            foreach(var p in processes)
            {
                string name;
                try{name=p.ProcessName;}catch{continue;}
                // Only electron (DeadLight's install path) and OpenRGB need a path; reading modules of every process is slow and often denied.
                string? path=null;
                if(name.Equals("electron",StringComparison.OrdinalIgnoreCase)||name.Equals("OpenRGB",StringComparison.OrdinalIgnoreCase))
                    try{path=p.MainModule?.FileName;}catch{}
                int id;
                try{id=p.Id;}catch{continue;}
                list.Add(new(id,name,path));
            }
            return LightingConflicts.Find(list,ownOpenRgbPid);
        }
        finally{foreach(var p in processes) p.Dispose();}
    }
}
