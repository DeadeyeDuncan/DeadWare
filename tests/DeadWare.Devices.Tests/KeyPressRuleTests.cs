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
    // Lighting 4: every file that may import Windows functions, with exactly the functions it may import (a trailing W is stripped).
    static readonly Dictionary<string,string[]> Pinned=new()
    {
        ["DeadWare.Devices/KeyPressHook.cs"]=["SetWindowsHookEx","UnhookWindowsHookEx","CallNextHookEx","GetMessage","PostThreadMessage","GetModuleHandle","GetCurrentThreadId"],
        ["DeadWare.Devices/EverydayWatcher.cs"]=["RegisterClassEx","UnregisterClass","CreateWindowEx","DestroyWindow","DefWindowProc","GetMessage","DispatchMessage","PostMessage","PostQuitMessage",
            "RegisterHotKey","UnregisterHotKey","WTSRegisterSessionNotification","WTSUnRegisterSessionNotification","RegisterSuspendResumeNotification","UnregisterSuspendResumeNotification","GetModuleHandle",
            "SetTimer","KillTimer"],
        ["DeadWare.Devices/InputClock.cs"]=["GetLastInputInfo","GetTickCount","GetCursorPos"],
    };
    static readonly string[] Dynamic=["NativeLibrary","GetProcAddress","delegate* unmanaged","delegate*unmanaged"];
    static readonly string[] Forbidden=["ToUnicode","ToAscii","MapVirtualKey","VkKeyScan","GetKeyState","GetAsyncKeyState","GetKeyboardState","GetKeyNameText","GetKeyboardLayout","GetRawInputData","RegisterRawInputDevices","Console.ReadKey",
        "GetCursorInfo","GetPhysicalCursorPos","GetMouseMovePointsEx"];      // fix wave 1, T-M1: other ways of reading the pointer

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

    // wbr-1 D5: KBDLLHOOKSTRUCT.flags as the callback reads them. Win32 values: LLKHF_EXTENDED 0x01, LLKHF_LOWER_IL_INJECTED 0x02,
    // LLKHF_INJECTED 0x10, LLKHF_ALTDOWN 0x20, LLKHF_UP 0x80. Either injected flag means sent by software; Alt down changes nothing.
    [Theory]
    [InlineData(0x00u,false,false,false)]
    [InlineData(0x01u,true,false,false)]
    [InlineData(0x02u,false,true,false)]
    [InlineData(0x10u,false,true,false)]
    [InlineData(0x80u,false,false,true)]
    [InlineData(0x20u,false,false,false)]
    [InlineData(0x11u,true,true,false)]
    [InlineData(0x81u,true,false,true)]
    [InlineData(0x92u,false,true,true)]
    [InlineData(0x12u,false,true,false)]
    [InlineData(0xA1u,true,false,true)]
    [InlineData(0xFFu,true,true,true)]
    public void The_hook_decodes_the_flags(uint flags,bool extended,bool sentBySoftware,bool up)
        =>Assert.Equal((extended,sentBySoftware,up),KeyPressHook.Decode(flags));

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
        // Fix round 1, M3: no binding to Windows functions by other means than the import attributes counted below.
        foreach(var (name,text) in all)
            foreach(var word in Dynamic)
                Assert.False(text.Contains(word,StringComparison.Ordinal),$"{name} binds a function by other means: {word}");
        var hook=all.Single(s=>s.Name=="DeadWare.Devices/KeyPressHook.cs").Text;
        Assert.DoesNotContain("vkCode",hook);
        Assert.Equal(Pinned["DeadWare.Devices/KeyPressHook.cs"].OrderBy(x=>x,StringComparer.Ordinal),Imports(hook));
        // Every import attribute in the file was counted by the pattern.
        Assert.Equal(Regex.Matches(hook,@"\[(?:DllImport|LibraryImport)\(").Count,Imports(hook).Length);
    }

    // Each imported entry point: the method declared after a DllImport or LibraryImport attribute, or its EntryPoint when one is given.
    static string[] Imports(string text)=>Regex.Matches(text,@"\[(?:DllImport|LibraryImport)\((?<args>[^\]]*)\)\][^;]*?\bextern\s+[\w<>\[\],\s]+?\s(?<name>\w+)\s*\(",RegexOptions.Singleline)
        .Select(m=>Regex.Match(m.Groups["args"].Value,@"EntryPoint\s*=\s*""(?<e>\w+)""") is{Success:true} e?e.Groups["e"].Value:m.Groups["name"].Value)
        .Select(n=>n.EndsWith('W')?n[..^1]:n).OrderBy(x=>x,StringComparer.Ordinal).ToArray();

    // Lighting 4: each pinned file imports exactly its list, and every import attribute in it is counted.
    [Fact]
    public void Each_pinned_file_imports_exactly_its_list()
    {
        var all=Sources();
        foreach(var (file,list) in Pinned)
        {
            var text=Assert.Single(all,s=>s.Name==file).Text;
            var imports=Imports(text);
            Assert.True(list.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(imports),$"{file} imports [{string.Join(",",imports)}], allowed [{string.Join(",",list.OrderBy(x=>x,StringComparer.Ordinal))}]");
            Assert.Equal(Regex.Matches(text,@"\[(?:DllImport|LibraryImport)\(").Count,imports.Length);
        }
    }

    // Lighting 4: no mouse hook, by any spelling of its constant.
    [Fact]
    public void No_file_names_a_mouse_hook()
    {
        foreach(var (name,text) in Sources())
            Assert.False(text.Contains("WH_MOUSE",StringComparison.Ordinal),$"{name} mentions WH_MOUSE");
    }

    // Fix wave 1, T-I1: no mouse hook by number either. The hook-install function is named only in KeyPressHook.cs (above), is called
    // there once, and with the keyboard low-level type, 13; a call with another type (14 or 7), or that constant changed, fails here.
    [Fact]
    public void The_one_hook_installed_is_the_keyboard_one()
    {
        var hook=Sources().Single(s=>s.Name=="DeadWare.Devices/KeyPressHook.cs").Text;
        var calls=Regex.Matches(hook,@"(?<!\bextern\s+[\w.]+\s+)\bSetWindowsHookExW?\s*\((?<first>[^,)]*)").Select(m=>m.Groups["first"].Value.Trim()).ToArray();
        Assert.Equal(["KeyboardLowLevel"],calls);
        Assert.Equal(13,typeof(KeyPressHook).GetField("KeyboardLowLevel",BindingFlags.NonPublic|BindingFlags.Static)!.GetRawConstantValue());
    }

    // Lighting 4, ruling P8: the shortcut, idle and pointer calls are named only in their own files, comments included.
    [Fact]
    public void The_shortcut_idle_and_pointer_calls_live_in_their_own_files()
    {
        var all=Sources();
        static string File(string n)=>n[(n.LastIndexOf('/')+1)..];
        string[] Where(string word)=>all.Where(s=>s.Text.Contains(word,StringComparison.Ordinal)).Select(s=>File(s.Name)).ToArray();
        Assert.Equal(["EverydayWatcher.cs"],Where("RegisterHotKey"));
        Assert.Equal(["InputClock.cs"],Where("GetLastInputInfo"));
        Assert.Equal(["InputClock.cs"],Where("GetCursorPos"));
    }
}
