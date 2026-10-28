using System.Windows;
using System.Windows.Media;
using DeadWare.Core;
namespace DeadWare.App;
internal static class Theme
{
    public static string CurrentName {get;private set;}="Midnight";
    public static bool IsDark=>CurrentName!="Daylight";
    public static Typeface ChartTypeface=>new((FontFamily)Application.Current.Resources["MetricFont"],FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    private static IReadOnlyDictionary<string,string> graphColors=new Dictionary<string,string>();
    public static string GraphKey(string kind)=>"Graph/"+(kind=="network"?"download":kind);
    public static void SetGraphColors(IReadOnlyDictionary<string,string> colors)
    {graphColors=GraphColorSettings.Normalize(colors);ApplyGraphColors();}
    private static void ApplyGraphColors()
    {
        foreach(string kind in GraphColorSettings.Kinds)
        {
            string role=kind switch {"gpu" or "gputemp"=>"Gpu","memory"=>"Accent","download"=>"Download",_=>"Cpu"};
            Application.Current.Resources[GraphKey(kind)]=!SystemParameters.HighContrast&&graphColors.TryGetValue(kind,out var color)
                ?new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)):Brush(role);
        }
    }
    // Binding source for the stock ScrollViewer corner; shared Style resources do not
    // reliably inherit application DynamicResource invalidation after a palette switch.
    public static SolidColorBrush ScrollCorner {get;}=new();
    public static readonly string[] Names=["Midnight","Ember","Glacier","Amethyst","Neon","Daylight"];
    private static readonly Dictionary<string,string[]> Palettes=new()
    {
        ["Midnight"]=["#0D1117","#151B24","#202B37","#354150","#DDE9F5","#9EB2C9","#91E7CA","#68E0BD","#F0B76C","#82ADFF","#F2CD91"],
        ["Ember"]=["#171112","#241B1D","#39292B","#65464B","#F3DCD4","#C7A79E","#FF9D8A","#89DDBD","#FFC27B","#9BBCFF","#F5D889"],
        ["Glacier"]=["#0C1721","#142632","#233B4B","#3C5C72","#D5ECF6","#9FBFCE","#8AD8FF","#79E4D0","#F3C680","#89B8FF","#F2D594"],
        ["Amethyst"]=["#161221","#231E31","#352C48","#5B4B75","#E8DDF6","#B7A3D0","#D4ACFF","#81DFBF","#F4C17C","#9CB6FF","#F4D18E"],
        ["Neon"]=["#070A12","#0E1422","#18243A","#2D4961","#C4E9F4","#82ADBF","#36F5FF","#69FFAD","#FF67E7","#66B3FF","#FFD166"],
        ["Daylight"]=["#EFF3F8","#FFFFFF","#E1E8F0","#B6C3D1","#243B50","#465D70","#245E8C","#08745D","#9B5709","#235CB3","#8D5700"]
    };
    static Theme()
    {
        SystemParameters.StaticPropertyChanged+=(_,e)=>
        {
            if(e.PropertyName!=nameof(SystemParameters.HighContrast))return;
            var app=Application.Current;
            if(app is not null&&!app.Dispatcher.HasShutdownStarted)app.Dispatcher.InvokeAsync(()=>Apply(CurrentName));
        };
    }
    public static void Apply(string name)
    {
        CurrentName=Palettes.ContainsKey(name)?name:"Midnight";
        var colors=Palettes[CurrentName];
        string[] keys=["Background","Panel","Control","Line","Ink","Muted","Accent","Cpu","Gpu","Download","Warning"];
        if(SystemParameters.HighContrast)
        {
            Brush[] system=[SystemColors.WindowBrush,SystemColors.WindowBrush,SystemColors.ControlBrush,SystemColors.WindowTextBrush,SystemColors.WindowTextBrush,SystemColors.GrayTextBrush,SystemColors.HighlightBrush,SystemColors.WindowTextBrush,SystemColors.WindowTextBrush,SystemColors.WindowTextBrush,SystemColors.WindowTextBrush];
            for(int i=0;i<keys.Length;i++)Application.Current.Resources[keys[i]]=system[i];
            Application.Current.Resources["ScrollThumb"]=SystemColors.ControlTextBrush;
            Application.Current.Resources["ScrollDisabled"]=SystemColors.GrayTextBrush;
        }
        else
        {
            for(int i=0;i<keys.Length;i++) Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            Application.Current.Resources["ScrollThumb"]=new SolidColorBrush(((SolidColorBrush)Brush("Muted")).Color) {Opacity=.7};
            Application.Current.Resources["ScrollDisabled"]=Brush("Line");
        }
        var (body,heading,metric)=CurrentName switch
        {
            "Ember"=>("Candara","Candara","Candara"),
            "Glacier"=>("Calibri","Calibri Light","Calibri"),
            "Amethyst"=>("Corbel","Corbel","Corbel"),
            "Neon"=>("Bahnschrift","Bahnschrift","Consolas"),
            _=>("Segoe UI","Segoe UI Semibold","Segoe UI")
        };
        FontFamily Font(string name)=>SystemParameters.HighContrast?SystemFonts.MessageFontFamily:new FontFamily(name=="Segoe UI"?name:name+", Segoe UI");
        Application.Current.Resources["BodyFont"]=Font(body);
        Application.Current.Resources["HeadingFont"]=Font(heading);
        Application.Current.Resources["MetricFont"]=Font(metric);
        Application.Current.Resources["Heading"]=SystemParameters.HighContrast?SystemColors.WindowTextBrush:Brush("Accent");
        Application.Current.Resources["ScrollThumbHover"]=SystemParameters.HighContrast?SystemColors.HighlightBrush:Brush("Muted");
        ScrollCorner.Color=((SolidColorBrush)Brush("Panel")).Color;
        ApplyGraphColors();
        NativeTheme.RefreshOpenWindows();
    }
    public static Brush Brush(string name)=>(Brush)Application.Current.Resources[name];
}
