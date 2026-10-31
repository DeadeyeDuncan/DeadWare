using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DeadWare.App;

internal sealed class ThemePicker : Menu
{
    private readonly MenuItem selected = new();
    private readonly Dictionary<string,MenuItem> choices = new(StringComparer.Ordinal);

    public ThemePicker(string current, Action<string> choose)
    {
        AutomationProperties.SetName(this,"Color theme");
        SetResourceReference(StyleProperty,"ThemeMenu");
        selected.SetResourceReference(StyleProperty,"ThemeMenuItem");
        AutomationProperties.SetName(selected,"Choose color theme");
        Items.Add(selected);
        MenuItem Choice(string name)
        {
            var item=new MenuItem {Header=name,IsCheckable=true};
            item.SetResourceReference(StyleProperty,"ThemeMenuItem");
            item.Click+=(_,e)=>{choose(name);SetSelection(name);e.Handled=true;};
            choices.Add(name,item);
            return item;
        }
        foreach(string name in Theme.Names.Where(name=>name is not ("Skyrim" or "Mage")))selected.Items.Add(Choice(name));
        var group=new MenuItem {Header="Elder Scrolls Themes"};
        group.SetResourceReference(StyleProperty,"ThemeMenuItem");
        group.Items.Add(Choice("Skyrim"));group.Items.Add(Choice("Mage"));
        selected.Items.Add(group);
        SetSelection(current);
    }

    public void SetSelection(string name)
    {
        selected.Header=name;
        foreach(var (theme,item) in choices)item.IsChecked=theme==name;
    }
}
