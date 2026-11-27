using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeadLightRetireFailedTests
{
    [Fact] public void An_exception_from_the_Windows_calls_is_a_failure_line_with_its_reason()
    {
        var result=DeadLightRetire.Failed(new InvalidOperationException("access denied"));
        Assert.False(result.Ok);
        Assert.Equal("DeadWare couldn't retire DeadLight: access denied.",result.Line);
    }
    [Fact] public void The_line_ends_in_one_full_stop_and_says_something_without_a_reason()
    {
        Assert.Equal("DeadWare couldn't retire DeadLight: access denied.",DeadLightRetire.Failed(new InvalidOperationException("access denied. ")).Line);
        Assert.Equal("DeadWare couldn't retire DeadLight: no reason was given.",DeadLightRetire.Failed(new InvalidOperationException("")).Line);
    }
}
