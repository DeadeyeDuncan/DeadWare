namespace DeadWare.Core;

public static class PopoutGraphMode
{
    public static string Resolve(string kind,bool joined)=>joined?kind switch
    {
        "cpu" or "gpu"=>"usage",
        "cputemp" or "gputemp"=>"temperature",
        _=>kind
    }:kind;
}
