using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.UpdateCore;
using Xunit;
namespace DeadWare.Tests;

public class ProtectedUpdateRootTests
{
    private const string SystemSid="S-1-5-18",Admins="S-1-5-32-544",Users="S-1-5-32-545",User="S-1-5-21-123-456-789-1001",Key="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Refusal=@"The update folder %ProgramData%\DeadWare\updates is not restricted to administrators. Delete it, then try again.";
    private const string ProgramData=@"C:\ProgramData",DeadWare=ProgramData+@"\DeadWare",Updates=DeadWare+@"\updates",Root=Updates+@"\"+Key,Job=Root+@"\jobs\00000000000000000000000000000001";
    private static PathSecurity Parent(string path)=>new(path,PathKind.Ancestor,SystemSid,[new(SystemSid,0x1F01FF,true,false),new(Admins,0x1F01FF,true,false),new(Users,0x1200A9,true,false),new(Users,0x116,true,false),new("S-1-3-0",0x1F01FF,true,true)],false);
    // Descendants inherit the creation DACL; DeadWare and updates carry it explicitly.
    [SupportedOSPlatform("windows")]
    private static PathSecurity Folder(string path,PathKind kind=PathKind.Directory,params AclRule[] extra)
    {var entry=InstallLocation.Describe(path,kind,ProtectedRoot.Security());return entry with {Rules=[..entry.Rules,..extra]};}
    [SupportedOSPlatform("windows")]
    private static PathSecurity[] Chain(PathSecurity? updates=null,PathSecurity? job=null,PathSecurity? file=null)=>
        [Folder(Root),job??Folder(Job),file??Folder(Job+@"\request.json",PathKind.File),updates??Folder(Updates),Folder(DeadWare),Parent(ProgramData),Parent(@"C:\")];

    [Fact, SupportedOSPlatform("windows")] public void Factory_keeps_the_per_user_base_for_an_unprotected_install()
    {
        using var fixture=new UpdateFixture();string installed=fixture.Package("app","0.6.0","old");
        var paths=UpdatePaths.ForInstallation(installed);
        Assert.False(paths.Protected);Assert.Null(paths.Legacy);
        Assert.Equal(new UpdatePaths(installed).Root,paths.Root);Assert.StartsWith(UpdatePaths.CacheBase+Path.DirectorySeparatorChar,paths.Root);
    }
    [Fact] public void Factory_moves_a_protected_install_to_the_administrator_base_and_keeps_its_legacy_root()
    {
        using var fixture=new UpdateFixture();string installed=fixture.Package("app","0.6.0","old"),protectedBase=Path.Combine(fixture.Root,"protected"),userBase=Path.Combine(fixture.Root,"user");
        var paths=UpdatePaths.ForInstallation(installed,true,protectedBase,userBase);
        Assert.True(paths.Protected);Assert.Equal(UpdatePaths.RootForKey(paths.Key,protectedBase),paths.Root);
        Assert.False(paths.Legacy!.Protected);Assert.Null(paths.Legacy.Legacy);Assert.Equal(UpdatePaths.RootForKey(paths.Key,userBase),paths.Legacy.Root);
        var user=UpdatePaths.ForInstallation(installed,false,protectedBase,userBase);
        Assert.False(user.Protected);Assert.Null(user.Legacy);Assert.Equal(paths.Legacy.Root,user.Root);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"DeadWare","updates"),UpdatePaths.ProtectedCacheBase);
        Assert.False(Directory.Exists(protectedBase));Assert.False(Directory.Exists(userBase));
    }
    [Fact] public void Helper_root_comes_from_its_own_path_under_one_of_the_two_bases()
    {
        using var fixture=new UpdateFixture();string id=Guid.NewGuid().ToString("N"),protectedBase=Path.Combine(fixture.Root,"protected"),userBase=Path.Combine(fixture.Root,"user");
        foreach(string cacheBase in new[]{protectedBase,userBase})
        {
            string root=UpdatePaths.RootForKey(Key,cacheBase);
            Assert.Equal(root,UpdatePaths.HelperRoot(Path.Combine(root,"jobs",id,"helper.exe"),Key,id,protectedBase,userBase));
        }
        string helper=Path.Combine(UpdatePaths.RootForKey(Key,userBase),"jobs",id,"helper.exe");
        Assert.Throws<InvalidDataException>(()=>UpdatePaths.HelperRoot(helper,new string('f',64),id,protectedBase,userBase));
        Assert.Throws<InvalidDataException>(()=>UpdatePaths.HelperRoot(helper,Key,Guid.NewGuid().ToString("N"),protectedBase,userBase));
        Assert.Throws<InvalidDataException>(()=>UpdatePaths.HelperRoot(helper,Key,id,protectedBase));
        Assert.Throws<InvalidDataException>(()=>UpdatePaths.HelperRoot(Path.Combine(Path.GetDirectoryName(helper)!,"DeadWare.Updater.exe"),Key,id,protectedBase,userBase));
        Assert.Throws<InvalidDataException>(()=>UpdatePaths.HelperRoot(Path.Combine(fixture.Root,"app","helper.exe"),Key,id,protectedBase,userBase));
    }
    [Fact, SupportedOSPlatform("windows")] public void Creation_dacl_is_administrator_owned_inheritance_protected_and_passes_the_policy()
    {
        var security=ProtectedRoot.Security();
        Assert.True(security.AreAccessRulesProtected);Assert.Equal(Admins,security.GetOwner(typeof(SecurityIdentifier))!.Value);
        var rules=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule=>(rule.IdentityReference.Value,rule.FileSystemRights,rule.AccessControlType,rule.InheritanceFlags,rule.PropagationFlags)).OrderBy(rule=>rule.Value).ToArray();
        const InheritanceFlags inherit=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        (string,FileSystemRights,AccessControlType,InheritanceFlags,PropagationFlags)[] expected=[(SystemSid,FileSystemRights.FullControl,AccessControlType.Allow,inherit,PropagationFlags.None),
            (Admins,FileSystemRights.FullControl,AccessControlType.Allow,inherit,PropagationFlags.None),(Users,FileSystemRights.ReadAndExecute|FileSystemRights.Synchronize,AccessControlType.Allow,inherit,PropagationFlags.None)];
        Assert.Equal(expected,rules);
        ProtectedRoot.Verify(Chain());
    }
    [Fact, SupportedOSPlatform("windows")] public void Verification_rejects_a_protected_root_candidate_that_users_can_change()
    {
        var write=new AclRule(Users,0x2,true,false);
        Assert.Equal(Refusal,Assert.Throws<InvalidDataException>(()=>ProtectedRoot.Verify(Chain(updates:Folder(Updates,PathKind.Directory,write)))).Message);
        Assert.Equal(Refusal,Assert.Throws<InvalidDataException>(()=>ProtectedRoot.Verify(Chain(file:Folder(Job+@"\request.json",PathKind.File,write)))).Message);
        Assert.Equal(Refusal,Assert.Throws<InvalidDataException>(()=>ProtectedRoot.Verify(Chain(job:Folder(Job) with {Owner=User}))).Message);
        Assert.Equal(Refusal,Assert.Throws<InvalidDataException>(()=>ProtectedRoot.Verify(Chain(job:Folder(Job,PathKind.Directory,new AclRule(User,0x10000000,true,true))))).Message);
        Assert.Equal(Refusal,Assert.Throws<InvalidDataException>(()=>ProtectedRoot.Verify(Array.Empty<PathSecurity>())).Message);
    }
    [Fact, SupportedOSPlatform("windows")] public void A_protected_root_under_a_user_writable_parent_is_never_prepared()
    {
        using var fixture=new UpdateFixture();string programData=Path.Combine(fixture.Root,"ProgramData");Directory.CreateDirectory(programData);
        var paths=UpdatePaths.ForInstallation(fixture.Package("app","0.6.0","old"),true,Path.Combine(programData,"DeadWare","updates"),Path.Combine(fixture.Root,"user"));
        // Unelevated test runs stop at the administrator requirement; elevated runs create DeadWare and then refuse the user-writable parents.
        var error=Record.Exception(()=>ProtectedRoot.Prepare(paths));
        Assert.True(error is UnauthorizedAccessException {Message:ProtectedRoot.Elevation} or InvalidDataException {Message:Refusal},error?.ToString());
        Assert.Contains("Restart as administrator",ProtectedRoot.Elevation);
        Assert.False(Directory.Exists(paths.Root));Assert.False(Directory.Exists(Path.Combine(fixture.Root,"user")));
    }
    [Fact, SupportedOSPlatform("windows")] public void A_junction_under_the_protected_base_is_refused_without_breaking_construction_or_the_per_user_helper()
    {
        using var fixture=new UpdateFixture();string target=Path.Combine(fixture.Root,"elsewhere"),programData=Path.Combine(fixture.Root,"ProgramData"),link=Path.Combine(programData,"DeadWare");
        string protectedBase=Path.Combine(link,"updates"),userBase=Path.Combine(fixture.Root,"user"),id=Guid.NewGuid().ToString("N"),installed=fixture.Package("app","0.6.0","old");
        ProtectedRoot.VerifyExisting(UpdatePaths.ForInstallation(installed,true,protectedBase,userBase)); // Nothing to trust or refuse before DeadWare exists.
        Directory.CreateDirectory(target);Directory.CreateDirectory(programData);Junction(link,target);
        try
        {
            // Any local user can plant this junction under %ProgramData%.
            var paths=UpdatePaths.ForInstallation(installed,true,protectedBase,userBase);
            Assert.Equal(Path.Combine(protectedBase,paths.Key),paths.Root);Assert.Equal(paths.Root,UpdatePaths.LexicalRoot(paths.Key,protectedBase));
            Assert.Equal(Refusal,Assert.Throws<InvalidDataException>(()=>ProtectedRoot.VerifyExisting(paths)).Message);
            var prepared=Record.Exception(()=>ProtectedRoot.Prepare(paths));
            Assert.True(prepared is UnauthorizedAccessException {Message:ProtectedRoot.Elevation} or InvalidDataException {Message:Refusal},prepared?.ToString());
            string userRoot=UpdatePaths.RootForKey(Key,userBase);
            Assert.Equal(userRoot,UpdatePaths.HelperRoot(Path.Combine(userRoot,"jobs",id,"helper.exe"),Key,id,protectedBase,userBase));
            Assert.Throws<InvalidDataException>(()=>UpdatePaths.HelperRoot(Path.Combine(protectedBase,Key,"jobs",id,"helper.exe"),Key,id,protectedBase,userBase));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally{if(Directory.Exists(link))Directory.Delete(link);}
    }
    private static void Junction(string link,string target)
    {
        using var process=Process.Start(new ProcessStartInfo("cmd.exe"){ArgumentList={"/c","mklink","/J",link,target},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true})!;
        process.StandardOutput.ReadToEnd();process.WaitForExit();Assert.Equal(0,process.ExitCode);
    }
    [Fact, SupportedOSPlatform("windows")] public void Unprotected_paths_are_neither_prepared_nor_audited()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(fixture.Package("app","0.6.0","old"),Path.Combine(fixture.Root,"cache"));
        ProtectedRoot.Prepare(paths);ProtectedRoot.Verify(paths);ProtectedRoot.VerifyExisting(paths);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root,"cache")));
    }
    [Fact] public void Locked_helper_cannot_be_replaced_or_deleted_but_still_starts()
    {
        using var fixture=new UpdateFixture();string helper=Path.Combine(fixture.Root,"helper.exe"),other=Path.Combine(fixture.Root,"other.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory,"cmd.exe"),helper);File.WriteAllText(other,"replacement");
        using(var locked=UpdateHandoff.LockHelper(helper,Packages.Hash(helper)))
        {
            Assert.True(locked.CanRead);Assert.False(locked.CanWrite);
            foreach(var change in new Action[]{()=>File.Copy(other,helper,true),()=>File.Delete(helper),()=>File.Move(helper,Path.Combine(fixture.Root,"moved.exe")),
                ()=>File.Move(other,helper,true),()=>new FileStream(helper,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete).Dispose()})
                Assert.True(Record.Exception(change) is IOException or UnauthorizedAccessException);
            using(var process=Process.Start(new ProcessStartInfo(helper){ArgumentList={"/c","exit","0"},UseShellExecute=false,CreateNoWindow=true})!)
            {Assert.True(process.WaitForExit(10000));Assert.Equal(0,process.ExitCode);}
            Assert.Equal(Packages.Hash(Path.Combine(Environment.SystemDirectory,"cmd.exe")),Packages.Hash(helper));
        }
        File.Delete(helper);Assert.False(File.Exists(helper));
    }
    [Fact] public void A_helper_that_fails_verification_is_not_kept_locked()
    {
        using var fixture=new UpdateFixture();string helper=Path.Combine(fixture.Root,"helper.exe");File.WriteAllText(helper,"tampered");
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.LockHelper(helper,new string('0',64)));
        File.Delete(helper);Assert.False(File.Exists(helper));
    }
}
