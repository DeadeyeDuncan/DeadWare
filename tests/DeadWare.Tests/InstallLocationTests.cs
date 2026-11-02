using System.Diagnostics;
using System.Runtime.Versioning;
using DeadWare.UpdateCore;
using Xunit;
namespace DeadWare.Tests;

public class InstallLocationTests
{
    private const string SystemSid="S-1-5-18",Admins="S-1-5-32-544",Installer="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",Users="S-1-5-32-545",AuthenticatedUsers="S-1-5-11",User="S-1-5-21-123-456-789-1001";
    private const string Root=@"C:\Program Files\DeadWare",Runtimes=Root+@"\runtimes",Dll=Root+@"\DeadWare.dll",ProgramFiles=@"C:\Program Files";
    // Program Files' own DACL as inherited by a new subfolder (icacls): explicit full control plus inheritable generic grants.
    private static readonly AclRule[] Inherited=[new(Installer,0x1F01FF,true,false),new(Installer,0x10000000,true,true),new(SystemSid,0x1F01FF,true,false),new(SystemSid,0x10000000,true,true),
        new(Admins,0x1F01FF,true,false),new(Admins,0x10000000,true,true),new(Users,0x1200A9,true,false),new(Users,unchecked((int)0xA0000000),true,true),new("S-1-3-0",0x10000000,true,true),
        new("S-1-15-2-1",0x1200A9,true,false),new("S-1-15-2-1",unchecked((int)0xA0000000),true,true)];
    private static PathSecurity Folder(string path,params AclRule[] extra)=>new(path,PathKind.Directory,Admins,[..Inherited,..extra],false);
    private static PathSecurity Item(string path,params AclRule[] extra)=>new(path,PathKind.File,Admins,[new(SystemSid,0x1F01FF,true,false),new(Admins,0x1F01FF,true,false),new(Users,0x1200A9,true,false),..extra],false);
    private static PathSecurity Parent(string path,params AclRule[] extra)=>new(path,PathKind.Ancestor,Installer,[new(SystemSid,0x1F01FF,true,false),new(Admins,0x1F01FF,true,false),new(Users,0x1200A9,true,false),..extra],false);
    private static PathSecurity[] Install(PathSecurity? directory=null,PathSecurity? file=null,PathSecurity? ancestor=null,params PathSecurity[] extra)=>
        [directory??Folder(Root),Folder(Runtimes),file??Item(Dll),Item(Root+@"\deadware-package.json"),Item(Root+@"\DeadWare.exe"),..extra,ancestor??Parent(ProgramFiles),Parent(@"C:\",new(AuthenticatedUsers,0x4,true,false),new(AuthenticatedUsers,0x1301BF,true,true))];
    private static void Unprotected(IEnumerable<PathSecurity> entries,string path)
    {
        var result=InstallLocation.Evaluate(entries);
        Assert.False(result.Protected);Assert.Contains(path,result.Reason);
    }

    [Fact] public void Trusted_only_rules_with_program_files_inheritance_are_protected()
    {
        var result=InstallLocation.Evaluate(Install());
        Assert.True(result.Protected,result.Reason);
    }
    [Fact] public void Users_add_file_on_install_directory_is_unprotected()=>Unprotected(Install(directory:Folder(Root,new AclRule(Users,0x2,true,false))),Root);
    [Fact] public void Inherit_only_untrusted_rules_on_files_and_ancestors_are_ignored()
    {
        var write=new AclRule(Users,0x10000000,true,true);
        Assert.True(InstallLocation.Evaluate(Install(file:Item(Dll,write),ancestor:Parent(ProgramFiles,write))).Protected);
    }
    [Theory]
    [InlineData(0x2)]
    [InlineData(0x40000000)]
    [InlineData(0x10000000)]
    public void Inheritable_untrusted_write_on_install_directories_is_unprotected(int rights)
    {
        Unprotected(Install(directory:Folder(Root,new AclRule(Users,rights,true,true))),Root);
        Unprotected([Folder(Root),new(Runtimes,PathKind.Directory,Admins,[..Inherited,new(User,rights,true,true)],false),Parent(ProgramFiles)],Runtimes);
    }
    [Fact] public void Untrusted_owner_is_unprotected()
    {
        Unprotected(Install(directory:Folder(Root) with {Owner=User}),Root);
        Unprotected(Install(file:Item(Dll) with {Owner=User}),Dll);
        Unprotected(Install(ancestor:Parent(ProgramFiles) with {Owner=null}),ProgramFiles);
    }
    [Fact] public void Delete_child_on_an_ancestor_is_unprotected()=>Unprotected(Install(ancestor:Parent(ProgramFiles,new AclRule(AuthenticatedUsers,0x40,true,false))),ProgramFiles);
    [Fact] public void Ancestor_that_only_lets_users_create_folders_stays_protected()
    {
        Assert.True(InstallLocation.Evaluate(Install(ancestor:Parent(ProgramFiles,new AclRule(Users,0x2|0x4|0x40000000,true,false)))).Protected);
    }
    [Fact] public void Untrusted_write_on_one_managed_file_is_unprotected()
    {
        Unprotected(Install(file:Item(Dll,new AclRule(User,0x2,true,false))),Dll);
        Unprotected(Install(file:Item(Dll,new AclRule(Users,0x4,true,false))),Dll);
    }
    [Fact] public void Writable_file_missing_from_the_manifest_is_unprotected()
    {
        string plugin=Root+@"\runtimes\win-x64\native\plugin.dll";
        Unprotected(Install(extra:Item(plugin,new AclRule(Users,0x10000,true,false))),plugin);
    }
    [Fact] public void Reparse_point_anywhere_in_the_chain_is_unprotected()
    {
        Unprotected(Install(file:Item(Dll) with {ReparsePoint=true}),Dll);
        Unprotected(Install(ancestor:Parent(ProgramFiles) with {ReparsePoint=true}),ProgramFiles);
    }
    [Theory]
    [InlineData("S-1-3-4")]
    [InlineData("S-1-3-0")]
    public void Owner_placeholders_are_trusted_only_with_a_trusted_owner(string sid)
    {
        Assert.True(InstallLocation.Evaluate(Install(directory:Folder(Root,new AclRule(sid,0x1F01FF,true,false)))).Protected);
        Unprotected(Install(directory:Folder(Root,new AclRule(sid,0x1F01FF,true,false)) with {Owner=User}),Root);
        Unprotected(Install(file:Item(Dll,new AclRule(sid,0x2,true,false)) with {Owner=Users}),Dll);
    }
    [Fact] public void Deny_rules_never_offset_untrusted_writes()
    {
        Unprotected(Install(directory:Folder(Root,new AclRule(Users,0x2,true,false),new(Users,0x2,false,false))),Root);
        Assert.True(InstallLocation.Evaluate(Install(directory:Folder(Root,new AclRule(Users,0x1F01FF,false,false)))).Protected);
    }
    [Fact] public void Read_failures_and_empty_checks_are_unprotected()
    {
        Unprotected(Install(file:new(Dll,PathKind.File,null,[],false,"Access is denied.")),Dll);
        Assert.False(InstallLocation.Evaluate([]).Protected);
    }
    [Fact] public void Rights_masks_match_the_policy()
    {
        Assert.Equal(0x2|0x4|0x40|0x100|0x10000|0x40000|0x80000|0x40000000|0x10000000,InstallLocation.DirectoryRights);
        Assert.Equal(0x2|0x4|0x100|0x10000|0x40000|0x80000|0x40000000|0x10000000,InstallLocation.FileRights);
        Assert.Equal(0x40|0x10000|0x40000|0x80000|0x10000000,InstallLocation.AncestorRights);
    }
    [Fact] public void Write_attributes_on_a_subdirectory_or_file_is_unprotected()
    {
        // FILE_WRITE_ATTRIBUTES alone can turn an empty folder into a mount point.
        string staging=Root+@"\staging";
        Unprotected(Install(extra:Folder(staging,new AclRule(Users,0x100,true,false))),staging);
        Unprotected(Install(file:Item(Dll,new AclRule(Users,0x100,true,false))),Dll);
    }
    [Theory]
    [InlineData(@"\\server\share\DeadWare")]
    [InlineData(@"\\?\UNC\server\share\DeadWare")]
    public void Network_paths_are_unprotected_even_with_trusted_rules(string path)
    {
        var result=InstallLocation.Evaluate([Folder(path),Parent(@"\\server\share")]);
        Assert.False(result.Protected);Assert.Equal(path+" is not on a local drive.",result.Reason);
    }
    [Fact] public void Mapped_network_drive_is_unprotected_even_with_trusted_rules()
    {
        var result=InstallLocation.Evaluate(Install(directory:Folder(Root) with {Network=true}));
        Assert.False(result.Protected);Assert.Equal(Root+" is not on a local drive.",result.Reason);
    }
    [Theory, SupportedOSPlatform("windows")]
    [InlineData(@"\\server\share\DeadWare")]
    [InlineData(@"\\?\UNC\server\share\DeadWare")]
    public void Reader_rejects_network_paths_before_touching_them(string path)
    {
        var result=InstallLocation.Check(path);
        Assert.False(result.Protected);Assert.Equal(path+" is not on a local drive.",result.Reason);
    }
    [Fact, SupportedOSPlatform("windows")] public void Fresh_temp_directory_is_unprotected()
    {
        string directory=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"DeadWare.exe"),"app");
            var result=InstallLocation.Check(directory);
            Assert.False(result.Protected);Assert.Contains(directory,result.Reason);
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact, SupportedOSPlatform("windows")] public void Reader_audits_every_file_regardless_of_manifest_and_marks_parents()
    {
        string directory=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N")),nested=Path.Combine(directory,"runtimes","win-x64");
        try
        {
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(directory,Packages.ManifestName),"""{"Format":1,"Version":"0.6.39","Files":{"DeadWare.exe":"00"}}""");
            File.WriteAllText(Path.Combine(directory,"DeadWare.exe"),"app");File.WriteAllText(Path.Combine(nested,"unlisted.dll"),"extra");
            var entries=InstallLocation.Read(directory,1);
            Assert.All(entries,entry=>Assert.Null(entry.Error));
            Assert.Equal((directory,PathKind.Directory),(entries[0].Path,entries[0].Kind));
            foreach(string file in new[]{Path.Combine(directory,Packages.ManifestName),Path.Combine(directory,"DeadWare.exe"),Path.Combine(nested,"unlisted.dll")})
                Assert.Equal(PathKind.File,Assert.Single(entries,entry=>entry.Path==file).Kind);
            foreach(string folder in new[]{Path.Combine(directory,"runtimes"),nested,Path.GetDirectoryName(directory)!})
                Assert.Equal(PathKind.Directory,Assert.Single(entries,entry=>entry.Path==folder).Kind);
            Assert.Equal((Path.GetPathRoot(directory)!,PathKind.Ancestor),(entries[^1].Path,entries[^1].Kind));
            Assert.Equal(PathKind.Ancestor,InstallLocation.Read(directory).Single(entry=>entry.Path==Path.GetDirectoryName(directory)).Kind);
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact, SupportedOSPlatform("windows")] public void Reader_flags_junctions_without_following_them()
    {
        string directory=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N")),target=Path.Combine(directory,"target"),link=Path.Combine(directory,"link");
        try
        {
            Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"DeadWare.dll"),"app");
            using(var mklink=Process.Start(new ProcessStartInfo("cmd.exe"){ArgumentList={"/c","mklink","/J",link,target},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true})!){mklink.StandardOutput.ReadToEnd();mklink.WaitForExit();Assert.Equal(0,mklink.ExitCode);}
            var entries=InstallLocation.Read(directory);
            var junction=Assert.Single(entries,entry=>entry.Path==link);
            Assert.True(junction.ReparsePoint);Assert.Equal(PathKind.Directory,junction.Kind);
            Assert.DoesNotContain(entries,entry=>entry.Path.StartsWith(link+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
            Assert.Equal(link+" is a link or junction.",InstallLocation.Evaluate([junction]).Reason);
        }
        finally {if(Directory.Exists(link))Directory.Delete(link);if(Directory.Exists(directory))Directory.Delete(directory,true);} // Recursive delete unmounts junctions, which needs administrator rights.
    }
}
