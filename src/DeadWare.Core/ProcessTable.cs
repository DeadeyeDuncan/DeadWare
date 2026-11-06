namespace DeadWare.Core;
public static class ProcessCopy
{
    public static string ScanSentence(bool elevated,bool diskColumns,bool scanLoop)=>
        "Turning it on adds one Windows process scan per poll"+(scanLoop?" (at most one a second)":"")+(elevated&&diskColumns?" and a disk trace":"")+".";
}
