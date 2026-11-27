using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class HubSwitchTextPinTests
{
    // The hub switch's flow words an unanswered or unconnected request as the client does; the client reads its texts from the flow, and this pins both.
    [Fact] public void The_flows_texts_are_the_clients()
    {
        Assert.Equal("The lighting helper didn't answer.",DeviceHostClient.NoAnswerText);
        Assert.Equal(DeviceHostClient.NoAnswerText,HubSwitchFlow.NoAnswerText);
        Assert.Equal("The lighting helper isn't connected.",HubSwitchFlow.NoReplyText);
    }
}
