namespace DeadWare.Core;
public record DiagnosticFailure(string Phase,string Type,string HResult,string Message,string? StackTrace)
{
    public static DiagnosticFailure? Capture(string phase,Exception error,bool diagnosticsEnabled)=>diagnosticsEnabled
        ?new(phase,error.GetType().FullName??error.GetType().Name,$"0x{error.HResult:X8}",error.Message,error.StackTrace):null;
}
