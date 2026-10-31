using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DeadWare.Core;
namespace DeadWare.App;
internal static class Theme
{
    public static string CurrentName {get;private set;}="Midnight";
    public static bool IsDark=>CurrentName!="Daylight";
    public static bool GlassEnabled {get;private set;}
    // See-through windows reveal the blurred desktop through a Windows 11 acrylic backdrop.
    private static string glassLevel=GlassLevels.Balanced;
    public static string GlassLevel {get=>glassLevel;set=>glassLevel=GlassLevels.Normalize(value);}
    public static bool BackdropActive {get;private set;}
    public static bool ElderScrolls=>CurrentName is "Skyrim" or "Mage";
    public static FontFamily NordicFont {get;}=new(new Uri("pack://application:,,,/DeadWare;component/"),"./Assets/Fonts/#Caesar Dressing");
    public static FontFamily RuneFont {get;}=new(new Uri("pack://application:,,,/DeadWare;component/"),"./Assets/Fonts/#Noto Sans Runic");
    private static BitmapImage? stoneTexture;
    public static Typeface ChartTypeface=>new((FontFamily)Application.Current.Resources["MetricFont"],FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    private static IReadOnlyDictionary<string,string> graphColors=new Dictionary<string,string>();
    public static string GraphKey(string kind)=>"Graph/"+(kind=="network"?"download":kind);
    public static void SetGraphColors(IReadOnlyDictionary<string,string> colors)
    {graphColors=GraphColorSettings.Normalize(colors);ApplyGraphColors();}
    private static void ApplyGraphColors()
    {
        foreach(string kind in GraphColorSettings.Kinds)
        {
            string role=kind switch {"gpu" or "gputemp"=>"Gpu","gpufan"=>"Warning","memory"=>"Accent","download"=>"Download",_=>"Cpu"};
            Application.Current.Resources[GraphKey(kind)]=!SystemParameters.HighContrast&&graphColors.TryGetValue(kind,out var color)
                ?new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)):Brush(role);
        }
    }
    // Binding source for the stock ScrollViewer corner; shared Style resources do not
    // reliably inherit application DynamicResource invalidation after a palette switch.
    public static SolidColorBrush ScrollCorner {get;}=new();
    public static readonly string[] Names=["Midnight","Ember","Glacier","Amethyst","Neon","Blackout","Skyrim","Mage","Daylight"];
    private static readonly Dictionary<string,string[]> Palettes=new()
    {
        ["Midnight"]=["#0D1117","#151B24","#202B37","#354150","#DDE9F5","#9EB2C9","#91E7CA","#68E0BD","#F0B76C","#82ADFF","#F2CD91"],
        ["Ember"]=["#171112","#241B1D","#39292B","#65464B","#F3DCD4","#C7A79E","#FF9D8A","#89DDBD","#FFC27B","#9BBCFF","#F5D889"],
        ["Glacier"]=["#0C1721","#142632","#233B4B","#3C5C72","#D5ECF6","#9FBFCE","#8AD8FF","#79E4D0","#F3C680","#89B8FF","#F2D594"],
        ["Amethyst"]=["#161221","#231E31","#352C48","#5B4B75","#E8DDF6","#B7A3D0","#D4ACFF","#81DFBF","#F4C17C","#9CB6FF","#F4D18E"],
        ["Neon"]=["#070A12","#0E1422","#18243A","#2D4961","#C4E9F4","#82ADBF","#36F5FF","#69FFAD","#FF67E7","#66B3FF","#FFD166"],
        ["Blackout"]=["#000000","#000000","#121212","#303030","#EEEEEE","#A3A3A3","#D4D4D4","#80DAB5","#E8B87D","#8CB8F0","#E8CD80"],
        ["Skyrim"]=["#101315","#1B2023","#2A3135","#677177","#E4E5DF","#AEB8BE","#B3D9EC","#9FD4E5","#D6C49B","#A5BAEF","#E3BC7D"],
        ["Mage"]=["#12101D","#1D192C","#302840","#706083","#EDE6D9","#BBB0CE","#8FDCE6","#98E2D4","#CEADFA","#A2BFFF","#E2C184"],
        ["Daylight"]=["#EFF3F8","#FFFFFF","#E1E8F0","#B6C3D1","#243B50","#465D70","#245E8C","#08745D","#9B5709","#235CB3","#8D5700"]
    };
    static Theme()
    {
        SystemParameters.StaticPropertyChanged+=(_,e)=>
        {
            if(e.PropertyName!=nameof(SystemParameters.HighContrast))return;
            RefreshFromWindows();
        };
        // Windows broadcasts personalization changes, including its Transparency effects
        // preference. Replacing resources refreshes already-open windows and pop-ups.
        SystemEvents.UserPreferenceChanged+=(_,_)=>RefreshFromWindows();
    }
    private static void RefreshFromWindows()
    {
        var app=Application.Current;
        if(app is not null&&!app.Dispatcher.HasShutdownStarted)app.Dispatcher.InvokeAsync(()=>Apply(CurrentName));
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
            "Skyrim" or "Mage"=>("Nordic","Nordic","Segoe UI"),
            _=>("Segoe UI","Segoe UI Semibold","Segoe UI")
        };
        FontFamily Font(string name)=>SystemParameters.HighContrast?SystemFonts.MessageFontFamily:name=="Nordic"?NordicFont:new FontFamily(name=="Segoe UI"?name:name+", Segoe UI");
        Application.Current.Resources["BodyFont"]=Font(body);
        Application.Current.Resources["HeadingFont"]=Font(heading);
        Application.Current.Resources["MetricFont"]=Font(metric);
        Application.Current.Resources["Heading"]=SystemParameters.HighContrast?SystemColors.WindowTextBrush:Brush(CurrentName switch {"Skyrim"=>"Ink","Mage"=>"Warning",_=>"Accent"});
        Application.Current.Resources["ScrollThumbHover"]=SystemParameters.HighContrast?SystemColors.HighlightBrush:Brush("Muted");
        ScrollCorner.Color=((SolidColorBrush)Brush("Panel")).Color;
        ApplyGlassResources();
        ApplyGraphColors();
        NativeTheme.RefreshOpenWindows();
    }
    private static bool TransparencyAllowed()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value||value!=0;
        }
        catch(Exception error) when(error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {return false;}
    }
    private static void ApplyGlassResources()
    {
        GlassEnabled=!SystemParameters.HighContrast&&CurrentName!="Blackout"&&TransparencyAllowed();
        BackdropActive=GlassEnabled&&GlassLevel!=GlassLevels.Off&&NativeTheme.BackdropSupported;
        var resources=Application.Current.Resources;
        if(!GlassEnabled)
        {
            // Keep all fallback surfaces opaque. In particular, Blackout's window,
            // panels, tables and pop-ups stay pure black, without a highlight wash.
            resources["WindowSurface"]=Brush("Background");
            resources["GlassPanel"]=Brush("Panel");
            resources["GlassBorder"]=Brush("Line");
            resources["GlassControl"]=Brush("Control");
            resources["GlassHover"]=SystemParameters.HighContrast?SystemColors.WindowBrush:Brush("Control");
            resources["GlassSelected"]=SystemParameters.HighContrast?SystemColors.WindowBrush:Brush("Control");
            resources["GlassInput"]=SystemParameters.HighContrast?SystemColors.WindowBrush:Brush("Background");
            resources["GlassPopup"]=Brush("Panel");
            resources["GlassTable"]=Brush("Panel");
            return;
        }
        Color ColorOf(string key)=>((SolidColorBrush)Brush(key)).Color;
        var background=ColorOf("Background");var panel=ColorOf("Panel");var control=ColorOf("Control");
        var accent=ColorOf("Accent");var gpu=ColorOf("Gpu");var line=ColorOf("Line");
        bool light=CurrentName=="Daylight";

        // Without a backdrop, an opaque atmospheric foundation gives the translucent card
        // layers something to reveal. With one, the tinted foundation lets the blurred desktop
        // through and the card layers thin out by the same chosen amount.
        var (windowAlpha,cardAlpha)=!BackdropActive?(1d,1d):GlassLevel switch
        {
            GlassLevels.Subtle=>(.78,.88),
            GlassLevels.Strong=>(light ? .34 : .25,light ? .58 : .5),
            _=>(light ? .58 : .5,light ? .74 : .68)
        };
        double controlAlpha=(1+cardAlpha)/2;
        var drawing=new DrawingGroup();var bounds=new Rect(0,0,1,1);
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Alpha(background,windowAlpha)),null,new RectangleGeometry(bounds)));
        drawing.Children.Add(new GeometryDrawing(Bloom(accent,light ? .13 : .11,new Point(.08,.02),.95,.85),null,new RectangleGeometry(bounds)));
        drawing.Children.Add(new GeometryDrawing(Bloom(gpu,light ? .055 : .065,new Point(.98,.95),.8,.95),null,new RectangleGeometry(bounds)));
        var window=new DrawingBrush(drawing) {Viewbox=bounds,ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};
        window.Freeze();resources["WindowSurface"]=window;

        resources["GlassPanel"]=Surface(panel,accent,light ? .035 : .07,.94*cardAlpha,(light ? .84 : .87)*cardAlpha,(light ? .91 : .93)*cardAlpha);
        resources["GlassControl"]=Surface(control,accent,light ? .025 : .045,.96*controlAlpha,.88*controlAlpha,.94*controlAlpha);
        resources["GlassHover"]=Surface(Mix(control,accent,.12),accent,.08,.98*controlAlpha,.95*controlAlpha,.98*controlAlpha);
        resources["GlassSelected"]=Surface(Mix(control,accent,light ? .19 : .2),accent,.1,.99*controlAlpha,.97*controlAlpha,.99*controlAlpha);
        resources["GlassInput"]=Surface(Mix(background,panel,.5),accent,light ? .015 : .035,.96*controlAlpha,.91*controlAlpha,.96*controlAlpha);
        // Pop-ups are separate layered windows without a backdrop, so they stay solid.
        resources["GlassPopup"]=Surface(panel,accent,light ? .025 : .045,1,1,1);
        resources["GlassTable"]=Surface(panel,accent,light ? .015 : .03,.93*cardAlpha,.86*cardAlpha,.93*cardAlpha);

        // A brighter upper rim catches light; the lower rim recedes into the card.
        var border=new LinearGradientBrush {StartPoint=new Point(0,0),EndPoint=new Point(.2,1)};
        // Over a live backdrop the rim reads as a specular edge, so it brightens further.
        border.GradientStops.Add(new(Alpha(Mix(line,Colors.White,light ? .66 : BackdropActive ? .55 : .36),light ? .96 : BackdropActive ? .85 : .75),0));
        border.GradientStops.Add(new(Alpha(Mix(line,accent,.1),light ? .58 : .65),.28));
        border.GradientStops.Add(new(Alpha(line,light ? .5 : .52),1));
        border.Freeze();resources["GlassBorder"]=border;
        if(ElderScrolls)
        {
            resources["WindowSurface"]=StoneSurface(BackdropActive?window:Brush("Background"),.32*windowAlpha);
            resources["GlassPanel"]=StoneSurface((Brush)resources["GlassPanel"],.22);
            resources["GlassPopup"]=StoneSurface((Brush)resources["GlassPopup"],.12);
        }
    }
    private static DrawingBrush StoneSurface(Brush foundation,double opacity)
    {
        if(stoneTexture is null)
        {
            stoneTexture=new BitmapImage();stoneTexture.BeginInit();
            stoneTexture.UriSource=new Uri("pack://application:,,,/DeadWare;component/Assets/themes/nordic-stone.png");
            stoneTexture.CacheOption=BitmapCacheOption.OnLoad;stoneTexture.EndInit();stoneTexture.Freeze();
        }
        var bounds=new Rect(0,0,1,1);
        var grain=new ImageBrush(stoneTexture) {TileMode=TileMode.FlipXY,ViewportUnits=BrushMappingMode.RelativeToBoundingBox,Viewport=new Rect(0,0,1,1),Opacity=opacity};
        grain.Freeze();
        var drawing=new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(foundation,null,new RectangleGeometry(bounds)));
        drawing.Children.Add(new GeometryDrawing(grain,null,new RectangleGeometry(bounds)));
        var result=new DrawingBrush(drawing) {Viewbox=bounds,ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};
        result.Freeze();return result;
    }
    private static LinearGradientBrush Surface(Color basis,Color tint,double tintAmount,double topAlpha,double middleAlpha,double bottomAlpha)
    {
        bool light=CurrentName=="Daylight";
        var brush=new LinearGradientBrush {StartPoint=new Point(0,0),EndPoint=new Point(.12,1)};
        brush.GradientStops.Add(new(Alpha(Mix(basis,Colors.White,light ? .36 : .055),topAlpha),0));
        brush.GradientStops.Add(new(Alpha(Mix(basis,tint,tintAmount),middleAlpha),.22));
        brush.GradientStops.Add(new(Alpha(basis,bottomAlpha),1));
        brush.Freeze();return brush;
    }
    private static RadialGradientBrush Bloom(Color color,double opacity,Point center,double radiusX,double radiusY)
    {
        var brush=new RadialGradientBrush {Center=center,GradientOrigin=center,RadiusX=radiusX,RadiusY=radiusY};
        brush.GradientStops.Add(new(Alpha(color,opacity),0));
        brush.GradientStops.Add(new(Alpha(color,opacity*.35),.45));
        brush.GradientStops.Add(new(Alpha(color,0),1));
        brush.Freeze();return brush;
    }
    private static Color Alpha(Color color,double opacity)=>Color.FromArgb((byte)Math.Round(255*opacity),color.R,color.G,color.B);
    private static Color Mix(Color from,Color to,double amount)=>Color.FromRgb(
        (byte)Math.Round(from.R+(to.R-from.R)*amount),(byte)Math.Round(from.G+(to.G-from.G)*amount),(byte)Math.Round(from.B+(to.B-from.B)*amount));
    public static Brush Brush(string name)=>(Brush)Application.Current.Resources[name];
}
