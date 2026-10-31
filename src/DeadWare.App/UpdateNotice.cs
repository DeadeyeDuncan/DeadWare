using System.Windows;
using System.Windows.Controls;

namespace DeadWare.App;

/// <summary>Availability is separate from installation; this action only opens Updates.</summary>
internal sealed class UpdateNotice : Border
{
    private readonly TextBlock message=Ui.Text("",13);
    public UpdateNotice(Action review)
    {
        Visibility=Visibility.Collapsed;Padding=new(12);Margin=new(0,14,0,0);
        CornerRadius=new(10);BorderThickness=new(1);
        SetResourceReference(BackgroundProperty,"GlassPanel");SetResourceReference(BorderBrushProperty,"GlassBorder");
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        message.Margin=new(0,0,16,0);body.Children.Add(message);
        var button=Ui.Button("Review update",review);Grid.SetColumn(button,1);body.Children.Add(button);Child=body;
        System.Windows.Automation.AutomationProperties.SetName(this,"DeadWare update available");
    }
    internal void ApplyState(UpdateState state)
    {
        Visibility=state.HasNewerRelease?Visibility.Visible:Visibility.Collapsed;
        if(state.HasNewerRelease)message.Text=$"DeadWare {state.Release!.Version} is available.\nInstall when you are ready.";
    }
}
