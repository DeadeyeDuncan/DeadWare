using System.Windows;
using System.Windows.Media;
namespace DeadWare.App;
internal static class Theme
{
    public static readonly string[] Names=["Midnight","Ember","Glacier","Amethyst","Daylight"];
    private static readonly Dictionary<string,string[]> Palettes=new()
    {
        ["Midnight"]=["#0D1117","#151B24","#202B37","#354150","#EDF2F8","#A6B4C6","#91E7CA","#68E0BD","#F0B76C","#82ADFF","#F2CD91"],
        ["Ember"]=["#171112","#241B1D","#39292B","#65464B","#FFF1ED","#D0B3AE","#FF9D8A","#89DDBD","#FFC27B","#9BBCFF","#F5D889"],
        ["Glacier"]=["#0C1721","#142632","#233B4B","#3C5C72","#ECF6FF","#AAC4D7","#8AD8FF","#79E4D0","#F3C680","#89B8FF","#F2D594"],
        ["Amethyst"]=["#161221","#231E31","#352C48","#5B4B75","#F6F0FF","#BEB0D2","#D4ACFF","#81DFBF","#F4C17C","#9CB6FF","#F4D18E"],
        ["Daylight"]=["#EFF3F8","#FFFFFF","#E1E8F0","#B6C3D1","#182A3B","#4C6075","#245E8C","#08745D","#9B5709","#235CB3","#8D5700"]
    };
    public static void Apply(string name)
    {
        var colors=Palettes.GetValueOrDefault(name)??Palettes["Midnight"];
        string[] keys=["Background","Panel","Control","Line","Ink","Muted","Accent","Cpu","Gpu","Download","Warning"];
        for(int i=0;i<keys.Length;i++) Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    public static Brush Brush(string name)=>(Brush)Application.Current.Resources[name];
}
