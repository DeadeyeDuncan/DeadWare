using System.Reflection;
using System.Text.RegularExpressions;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 3b, the key-press rule (spec points 1 and 5): the press record's shape, and an allow-list scan over the sources of
// DeadWare.Devices and DeadWare.Core.
public sealed class KeyPressRuleTests
{
    static readonly string[] Allowed=["SetWindowsHookEx","UnhookWindowsHookEx","CallNextHookEx","GetMessage","PostThreadMessage","GetModuleHandle","GetCurrentThreadId"];
    static readonly string[] Forbidden=["ToUnicode","ToAscii","MapVirtualKey","VkKeyScan","GetKeyState","GetAsyncKeyState","GetKeyboardState","GetKeyNameText","GetKeyboardLayout","GetRawInputData","RegisterRawInputDevices","Console.ReadKey"];

    [Fact]
    public void The_press_record_holds_only_a_device_lights_and_a_time()
    {
        var t=typeof(LightPress);
        var props=t.GetProperties(BindingFlags.Public|BindingFlags.Instance).Select(p=>$"{p.Name}:{p.PropertyType.Name}").OrderBy(x=>x).ToArray();
        Assert.Equal(["AtMs:Double","DeviceKey:String","Lights:IReadOnlyList`1"],props);
        Assert.Equal(typeof(IReadOnlyList<int>),t.GetProperty("Lights")!.PropertyType);
        // Beyond them only the record's compiler-made EqualityContract (a Type, not data).
        Assert.DoesNotContain(t.GetProperties(BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Public),p=>p.GetMethod?.IsStatic==true);
        Assert.Equal(["EqualityContract"],t.GetProperties(BindingFlags.NonPublic|BindingFlags.Instance).Select(p=>p.Name));
        var fields=t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Select(f=>f.Name).OrderBy(x=>x).ToArray();
        Assert.Equal(["<AtMs>k__BackingField","<DeviceKey>k__BackingField","<Lights>k__BackingField"],fields);
    }

    [Fact]
    public void The_hook_types_are_not_public()
    {
        Assert.False(typeof(KeyPressGate).IsPublic);
        Assert.False(typeof(KeyPressHook).IsPublic);
        Assert.False(typeof(IKeyPressSource).IsPublic);
        Assert.False(typeof(KeyPressGate).IsVisible);
        Assert.False(typeof(KeyPressHook).IsVisible);
        Assert.False(typeof(IKeyPressSource).IsVisible);
    }

    static string Root()
    {
        var d=new DirectoryInfo(AppContext.BaseDirectory);
        while(d is not null&&!File.Exists(Path.Combine(d.FullName,"DeadWare.slnx"))) d=d.Parent;
        return d?.FullName??throw new InvalidOperationException("DeadWare.slnx not found above "+AppContext.BaseDirectory);
    }
    // Every .cs source of the two projects, build output left out.
    static IReadOnlyList<(string Name,string Text)> Sources()
    {
        var root=Root();
        var r=new List<(string,string)>();
        foreach(var project in new[]{"DeadWare.Devices","DeadWare.Core"})
        {
            var dir=Path.Combine(root,"src",project);
            foreach(var f in Directory.EnumerateFiles(dir,"*.cs",SearchOption.AllDirectories))
            {
                var rel=Path.GetRelativePath(dir,f);
                if(rel.StartsWith("bin"+Path.DirectorySeparatorChar)||rel.StartsWith("obj"+Path.DirectorySeparatorChar)) continue;
                r.Add((project+"/"+rel.Replace('\\','/'),File.ReadAllText(f)));
            }
        }
        return r;
    }

    [Fact]
    public void Only_the_hook_file_imports_the_hook_functions()
    {
        var all=Sources();
        Assert.Contains(all,s=>s.Name=="DeadWare.Devices/KeyPressHook.cs");
        static string File(string n)=>n[(n.LastIndexOf('/')+1)..];
        Assert.Equal(["KeyPressHook.cs"],all.Where(s=>s.Text.Contains("SetWindowsHookEx")).Select(s=>File(s.Name)));
        Assert.Equal(["KeyPressHook.cs"],all.Where(s=>s.Text.Contains(".OnKey(")).Select(s=>File(s.Name)));
        Assert.Equal(["DeviceHost.cs","KeyPressGate.cs","KeyPressHook.cs","Program.cs"],all.Where(s=>s.Text.Contains("IKeyPressSource")).Select(s=>File(s.Name)).OrderBy(x=>x,StringComparer.Ordinal));
        foreach(var (name,text) in all)
            foreach(var word in Forbidden)
                Assert.False(text.Contains(word,StringComparison.Ordinal),$"{name} mentions {word}");
        var hook=all.Single(s=>s.Name=="DeadWare.Devices/KeyPressHook.cs").Text;
        Assert.DoesNotContain("vkCode",hook);
        // Each imported entry point: the method declared after a DllImport or LibraryImport attribute, or its EntryPoint when one is given.
        var imports=Regex.Matches(hook,@"\[(?:DllImport|LibraryImport)\((?<args>[^\]]*)\)\][^;]*?\bextern\s+[\w<>\[\],\s]+?\s(?<name>\w+)\s*\(",RegexOptions.Singleline)
            .Select(m=>Regex.Match(m.Groups["args"].Value,@"EntryPoint\s*=\s*""(?<e>\w+)""") is{Success:true} e?e.Groups["e"].Value:m.Groups["name"].Value)
            .Select(n=>n.EndsWith('W')?n[..^1]:n).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        Assert.Equal(Allowed.OrderBy(x=>x,StringComparer.Ordinal),imports);
        // Every import attribute in the file was counted by the pattern above.
        Assert.Equal(Regex.Matches(hook,@"\[(?:DllImport|LibraryImport)\(").Count,imports.Length);
    }
}
