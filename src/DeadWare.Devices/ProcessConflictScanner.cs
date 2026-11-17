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
                string? path=null;
                try{path=p.MainModule?.FileName;}catch{}
                string name;
                try{name=p.ProcessName;}catch{continue;}
                int id;
                try{id=p.Id;}catch{continue;}
                list.Add(new(id,name,path));
            }
            return LightingConflicts.Find(list,ownOpenRgbPid);
        }
        finally{foreach(var p in processes) p.Dispose();}
    }
}
