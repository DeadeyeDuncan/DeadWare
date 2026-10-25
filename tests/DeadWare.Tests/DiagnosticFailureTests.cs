using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class DiagnosticFailureTests
{
    [Fact] public void NormalCollectionDoesNotRetainExceptionDetails()
    {Assert.Null(DiagnosticFailure.Capture("ETW startup",new InvalidOperationException("private detail"),false));}
    [Fact] public void ExplicitDiagnosticsPreserveExactFailureForLocalInvestigation()
    {
        try {throw new InvalidOperationException("specific failure");}
        catch(InvalidOperationException error)
        {
            var failure=Assert.IsType<DiagnosticFailure>(DiagnosticFailure.Capture("Enable kernel provider",error,true));
            Assert.Equal("Enable kernel provider",failure.Phase);
            Assert.Equal("System.InvalidOperationException",failure.Type);
            Assert.Equal("0x80131509",failure.HResult);
            Assert.Equal("specific failure",failure.Message);
            Assert.Contains(nameof(ExplicitDiagnosticsPreserveExactFailureForLocalInvestigation),failure.StackTrace);
        }
    }
}
