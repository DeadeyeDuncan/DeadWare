using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.Devices;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class ProtectedFoldersTests
{
    [Fact]
    public void Writable_temp_folder_is_not_protected()
    {
        var dir=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result=ProtectedFolders.CheckOpenRgbFolder(Path.Combine(dir,"OpenRGB.exe"));
            Assert.False(result.Protected);
        }
        finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public void Unelevated_uses_local_app_data_and_creates_it()
    {
        var root=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        try
        {
            var dir=ProtectedFolders.OpenRgbConfigDirectory(false,Path.Combine(root,"pd"),Path.Combine(root,"local"));
            Assert.Equal(Path.Combine(root,"local","DeadWare","openrgb"),dir);
            Assert.True(Directory.Exists(dir));
            Assert.False(Directory.Exists(Path.Combine(root,"pd")));
        }
        finally{if(Directory.Exists(root)) Directory.Delete(root,true);}
    }

    // Ruling AL: %ProgramData% lets Users create folders, so the check of %ProgramData%\DeadWare\openrgb must read DeadWare as a protected
    // folder and %ProgramData% as a parent, as UpdateCore's check of the sibling updates folder does. At a depth of 2 every stock
    // Windows installation fails and elevated lighting never starts.
    [Fact]
    public void Config_folder_check_reads_DeadWare_as_protected_and_ProgramData_as_a_parent()
    {
        var root=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        string programData=Path.Combine(root,"pd"),deadWare=Path.Combine(programData,"DeadWare"),dir=Path.Combine(deadWare,"openrgb");
        Directory.CreateDirectory(dir);
        try
        {
            var entries=InstallLocation.Read(dir,ProtectedFolders.StrictParents);
            Assert.Equal(PathKind.Directory,entries.Single(e=>e.Path.Equals(dir,StringComparison.OrdinalIgnoreCase)).Kind);
            Assert.Equal(PathKind.Directory,entries.Single(e=>e.Path.Equals(deadWare,StringComparison.OrdinalIgnoreCase)).Kind);
            Assert.Equal(PathKind.Ancestor,entries.Single(e=>e.Path.Equals(programData,StringComparison.OrdinalIgnoreCase)).Kind);
        }
        finally{Directory.Delete(root,true);}
    }

    // The same, end to end with real ACLs: a scratch stand-in for %ProgramData% carries %ProgramData%'s own DACL (Users may create folders
    // in it), and the elevated preparation must still succeed. Access only: copying the owner (SYSTEM) would need SeRestorePrivilege.
    [ElevatedFact]
    public void Elevated_config_folder_is_prepared_under_a_stock_ProgramData_acl()
    {
        var standIn=@"C:\dw-pd-"+Guid.NewGuid().ToString("N");
        var acl=new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).GetAccessControl(AccessControlSections.Access);
        new DirectoryInfo(standIn).Create(acl);
        try
        {
            var dir=ProtectedFolders.OpenRgbConfigDirectory(true,standIn,Path.Combine(standIn,"local"));
            Assert.Equal(Path.Combine(standIn,"DeadWare","openrgb"),dir);
            var verdict=InstallLocation.Check(dir,ProtectedFolders.StrictParents);
            Assert.True(verdict.Protected,verdict.Reason);
        }
        finally{if(Directory.Exists(standIn)) Directory.Delete(standIn,true);}
    }

    // Ruling AT: a host without administrator rights logs where it always did; only an elevated one moves its log to %ProgramData%.
    [Fact]
    public void Unelevated_log_folder_is_local_app_data()
    {
        var root=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(Path.Combine(root,"local","DeadWare","logs"),ProtectedFolders.LogDirectory(false,Path.Combine(root,"pd"),Path.Combine(root,"local"),UserSid));
            Assert.False(Directory.Exists(Path.Combine(root,"pd")));
        }
        finally{if(Directory.Exists(root)) Directory.Delete(root,true);}
    }

    static readonly string UserSid=WindowsIdentity.GetCurrent().User!.Value;
    const string SystemSid="S-1-5-18",AdministratorsSid="S-1-5-32-544",UsersSid="S-1-5-32-545";

    // B1/B20: an elevated host's log folder is its own user's: only SYSTEM and Administrators may change it, and only they and that user may
    // read the log files, whatever the logs folder above lets every user read. Other users may list the folder and read permissions, which
    // the updater's audit of %ProgramData%\DeadWare needs in a DeadWare without administrator rights. Owned by Administrators, it passes
    // the protected-folder policy.
    [Fact]
    public void Elevated_user_log_folder_lets_only_that_user_SYSTEM_and_Administrators_read_the_log()
    {
        var security=ProtectedFolders.UserLogSecurity(new SecurityIdentifier(UserSid));
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(AdministratorsSid,security.GetOwner(typeof(SecurityIdentifier))!.Value);
        var rules=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules,r=>Assert.Equal(AccessControlType.Allow,r.AccessControlType));
        string[] Sids(IEnumerable<FileSystemAccessRule> some)=>[..some.Select(r=>r.IdentityReference.Value).Distinct().Order(StringComparer.Ordinal)];
        string[] Sorted(params string[] sids)=>[..sids.Distinct().Order(StringComparer.Ordinal)];
        Assert.Equal(Sorted(UserSid,SystemSid,AdministratorsSid,UsersSid),Sids(rules));
        // The rules that reach the log files and let their contents be read.
        Assert.Equal(Sorted(UserSid,SystemSid,AdministratorsSid),Sids(rules.Where(r=>(r.InheritanceFlags&InheritanceFlags.ObjectInherit)!=0&&(r.FileSystemRights&FileSystemRights.ReadData)!=0)));
        // The rules that let anything be changed.
        Assert.Equal(Sorted(SystemSid,AdministratorsSid),Sids(rules.Where(r=>((int)r.FileSystemRights&InstallLocation.DirectoryRights)!=0)));
        Assert.All(rules.Where(r=>r.IdentityReference.Value==UsersSid),r=>Assert.Equal(0,(int)(r.FileSystemRights&~(FileSystemRights.ListDirectory|FileSystemRights.ReadAttributes|FileSystemRights.ReadPermissions|FileSystemRights.Synchronize))));
        Assert.All(rules.Where(r=>r.IdentityReference.Value==UsersSid&&(r.FileSystemRights&FileSystemRights.ListDirectory)!=0),r=>Assert.Equal(InheritanceFlags.None,r.InheritanceFlags));
        var verdict=InstallLocation.Evaluate([InstallLocation.Describe(@"C:\ProgramData\DeadWare\logs\"+UserSid,PathKind.Directory,security)]);
        Assert.True(verdict.Protected,verdict.Reason);
    }

    // The SID names a folder, so anything that isn't a SID is refused before a folder is touched.
    [Fact]
    public void Elevated_log_folder_refuses_a_user_that_is_not_a_SID()
    {
        var root=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<ArgumentException>(()=>ProtectedFolders.LogDirectory(true,Path.Combine(root,"pd"),Path.Combine(root,"local"),@"..\evil"));
            Assert.False(Directory.Exists(root));
        }
        finally{if(Directory.Exists(root)) Directory.Delete(root,true);}
    }

    // Ruling AT and B1/B20: an elevated host logs to %ProgramData%\DeadWare\logs\<its user's SID>, created protected like the openrgb folder,
    // so a program without administrator rights can't redirect the host's log writes and rotations through a junction, and other users
    // can't read it: the log names the user's paths and devices.
    [ElevatedFact]
    public void Elevated_log_folder_is_prepared_protected_and_private_under_ProgramData()
    {
        var standIn=@"C:\dw-pd-"+Guid.NewGuid().ToString("N");
        var acl=new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).GetAccessControl(AccessControlSections.Access);
        new DirectoryInfo(standIn).Create(acl);
        try
        {
            var dir=ProtectedFolders.LogDirectory(true,standIn,Path.Combine(standIn,"local"),UserSid);
            Assert.Equal(Path.Combine(standIn,"DeadWare","logs",UserSid),dir);
            var verdict=InstallLocation.Check(dir,ProtectedFolders.StrictParents+1);
            Assert.True(verdict.Protected,verdict.Reason);
            using(var log=new HostLog(dir)) log.Write("written");
            // Only this user, SYSTEM and Administrators can read the log; the folder and the file still pass the policy.
            var readers=new FileInfo(Path.Combine(dir,"devices.log")).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r=>r.AccessControlType==AccessControlType.Allow&&(r.FileSystemRights&FileSystemRights.ReadData)!=0).Select(r=>r.IdentityReference.Value).Distinct().Order(StringComparer.Ordinal);
            Assert.Equal(new[]{UserSid,SystemSid,AdministratorsSid}.Distinct().Order(StringComparer.Ordinal),readers);
            verdict=InstallLocation.Check(dir,ProtectedFolders.StrictParents+1);
            Assert.True(verdict.Protected,verdict.Reason);
        }
        finally{if(Directory.Exists(standIn)) Directory.Delete(standIn,true);}
    }

    // Polish round 2, item 6: logs\<user SID> made again with the logs folder's inherited rights (an administrator deleted it while a host
    // ran, say) still passes the check, since only administrators can change it, but lets every user read the log. The next start makes the
    // folder, and the log files in it, private again.
    [ElevatedFact]
    public void Elevated_log_folder_made_again_with_inherited_rights_is_private_again_at_the_next_start()
    {
        var standIn=@"C:\dw-pd-"+Guid.NewGuid().ToString("N");
        var acl=new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).GetAccessControl(AccessControlSections.Access);
        new DirectoryInfo(standIn).Create(acl);
        try
        {
            var dir=ProtectedFolders.LogDirectory(true,standIn,Path.Combine(standIn,"local"),UserSid);
            Directory.Delete(dir,true);
            Directory.CreateDirectory(dir);
            var file=Path.Combine(dir,"devices.log");
            File.WriteAllText(file,"written");
            Assert.Contains(UsersSid,Readers(file)); // the logs folder's rights: every user can read
            Assert.Equal(dir,ProtectedFolders.LogDirectory(true,standIn,Path.Combine(standIn,"local"),UserSid));
            Assert.True(new DirectoryInfo(dir).GetAccessControl().AreAccessRulesProtected);
            Assert.Equal(new[]{UserSid,SystemSid,AdministratorsSid}.Distinct().Order(StringComparer.Ordinal),Readers(file));
            var verdict=InstallLocation.Check(dir,ProtectedFolders.StrictParents+1);
            Assert.True(verdict.Protected,verdict.Reason);
        }
        finally{if(Directory.Exists(standIn)) Directory.Delete(standIn,true);}
    }
    // The accounts an Allow rule lets read the file's contents.
    static string[] Readers(string file)=>[..new FileInfo(file).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
        .Where(r=>r.AccessControlType==AccessControlType.Allow&&(r.FileSystemRights&FileSystemRights.ReadData)!=0).Select(r=>r.IdentityReference.Value).Distinct().Order(StringComparer.Ordinal)];

    // Polish item 7 (B4/B13/B18): when %ProgramData%\DeadWare itself fails the check, the refusal names that folder, not openrgb beneath it,
    // gives InstallLocation's reason, and points the remedy at it: deleting openrgb alone would change nothing. Made without administrator
    // rights, it is owned by this user, so the remedy is a new owner (round 2, item 2) and write access for administrators only, since a
    // folder made that way usually lets its maker write too (round 3, item 5), never write access alone; made by an elevated test run
    // (CI's), Administrators own it but this user can change it. Removing it is the alternative either way.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refused_DeadWare_folder_is_named_with_its_reason_and_remedy(bool openRgbExists)
    {
        var root=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        string programData=Path.Combine(root,"pd"),deadWare=Path.Combine(programData,"DeadWare");
        Directory.CreateDirectory(openRgbExists?Path.Combine(deadWare,"openrgb"):deadWare);
        try
        {
            var refusal=Assert.Throws<UnprotectedFolderException>(()=>ProtectedFolders.OpenRgbConfigDirectory(true,programData,Path.Combine(root,"local")));
            Assert.NotNull(refusal.Reason);
            Assert.StartsWith(deadWare+" ",refusal.Reason);
            var owned=refusal.Reason!.EndsWith(" is not owned by SYSTEM, Administrators or TrustedInstaller.",StringComparison.Ordinal);
            Assert.True(owned||refusal.Reason.StartsWith(deadWare+" can be changed by ",StringComparison.Ordinal),refusal.Reason);
            Assert.Equal(refusal.Reason.TrimEnd('.')+(owned?", so DeadWare won't start OpenRGB. Make Administrators its owner and give only administrators write access to it, "
                :", so programs without administrator rights can change it and DeadWare won't start OpenRGB. Give only administrators write access to it, ")
                +"or remove it, which needs administrator rights. Then turn lighting off and on.",refusal.Message);
            Assert.Equal(deadWare,refusal.Folder);
        }
        finally{Directory.Delete(root,true);}
    }

    // Polish round 2, item 2: a refusal names the path InstallLocation's reason names and fits its words and remedy to the fault and to where
    // that path is: DeadWare's folder or something deeper inside it may be removed, %ProgramData% and the drive above it never; a path the
    // check couldn't judge is one DeadWare can't check, never one that isn't restricted. The reasons are InstallLocation's own.
    const string ProgramDataDir=@"C:\ProgramData",DeadWareDir=@"C:\ProgramData\DeadWare",OpenRgbDir=@"C:\ProgramData\DeadWare\openrgb",
        Deeper=@"C:\ProgramData\DeadWare\updates\0.19.0\DeadWare.dll";
    static string Reason(string path,PathKind kind,string? owner=AdministratorsSid,AclRule[]? rules=null,bool link=false,string? error=null,bool network=false)
        =>InstallLocation.Evaluate([new PathSecurity(path,kind,owner,rules??[],link,error,network)]).Reason!;
    const string Denied="Access to the path is denied.",Drive=@"C:\",Inside=" Remove it, which needs administrator rights, then turn lighting off and on.",
        Restore=" Restore its standard permissions, then turn lighting off and on.",OrRemove="or remove it, which needs administrator rights. Then turn lighting off and on.";
    public static TheoryData<string,string> Refusals=>new()
    {
        // Programs without administrator rights can change it.
        {Reason(Deeper,PathKind.File,rules:[new(UsersSid,0x2,true,false)]),
            $"{Deeper} can be changed by {UsersSid}, so programs without administrator rights can change it and DeadWare won't start OpenRGB. Give only administrators write access to it, {OrRemove}"},
        {Reason(DeadWareDir,PathKind.Directory,rules:[new(UsersSid,0x2,true,false)]),
            $"{DeadWareDir} can be changed by {UsersSid}, so programs without administrator rights can change it and DeadWare won't start OpenRGB. Give only administrators write access to it, {OrRemove}"},
        {Reason(ProgramDataDir,PathKind.Ancestor,rules:[new(UsersSid,0x10000,true,false)]),
            $"{ProgramDataDir} can be changed by {UsersSid}, so programs without administrator rights can change it and DeadWare won't start OpenRGB.{Restore}"},
        {Reason(Drive,PathKind.Ancestor,rules:[new("S-1-5-11",0x40000,true,false)]),
            $"{Drive} can be changed by S-1-5-11, so programs without administrator rights can change it and DeadWare won't start OpenRGB.{Restore}"},
        // Another account owns it.
        {Reason(OpenRgbDir,PathKind.Directory,owner:"S-1-5-21-1-2-3-1001"),
            $"{OpenRgbDir} is not owned by SYSTEM, Administrators or TrustedInstaller, so DeadWare won't start OpenRGB. Make Administrators its owner and give only administrators write access to it, {OrRemove}"},
        {Reason(ProgramDataDir,PathKind.Ancestor,owner:"S-1-5-21-1-2-3-1001"),
            $"{ProgramDataDir} is not owned by SYSTEM, Administrators or TrustedInstaller, so DeadWare won't start OpenRGB.{Restore}"},
        // The check couldn't judge it.
        {Reason(OpenRgbDir,PathKind.Directory,link:true),
            $"DeadWare can't check {OpenRgbDir}, so it won't start OpenRGB: {OpenRgbDir} is a link or junction.{Inside}"},
        {Reason(Deeper,PathKind.File,owner:null,error:Denied),
            $"DeadWare can't check {Deeper}, so it won't start OpenRGB: Permissions for {Deeper} could not be read: {Denied}{Inside}"},
        {Reason(ProgramDataDir,PathKind.Ancestor,owner:null,error:Denied),
            $"DeadWare can't check {ProgramDataDir}, so it won't start OpenRGB: Permissions for {ProgramDataDir} could not be read: {Denied}{Restore}"},
        {Reason(ProgramDataDir,PathKind.Ancestor,link:true),
            $"DeadWare can't check {ProgramDataDir}, so it won't start OpenRGB: {ProgramDataDir} is a link or junction."},
        {Reason(ProgramDataDir,PathKind.Ancestor,owner:null,network:true),
            $"DeadWare can't check {ProgramDataDir}, so it won't start OpenRGB: {ProgramDataDir} is not on a local drive."},
        {InstallLocation.Evaluate([]).Reason!,
            $"DeadWare can't check {OpenRgbDir}, so it won't start OpenRGB: No folder permissions were checked."},
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refusal_names_the_path_at_fault_with_words_and_a_remedy_that_fit(string reason,string expected)
    {
        var message=ProtectedFolders.Refusal(DeadWareDir,OpenRgbDir,reason);
        Assert.Equal(expected,message);
        Assert.DoesNotContain("isn't restricted",message);
        // Never a folder above DeadWare's own to be removed, and never write access alone for a path another account owns: a new owner comes first.
        if(!message.Contains(DeadWareDir,StringComparison.OrdinalIgnoreCase)) Assert.DoesNotContain("remove",message,StringComparison.OrdinalIgnoreCase);
        if(reason.Contains(" is not owned by ",StringComparison.Ordinal)&&message.Contains("write access",StringComparison.Ordinal))
            Assert.Contains("Make Administrators its owner and give only administrators write access to it",message);
    }

    // The log folder's refusal matches the OpenRGB folder's: only the outcome differs.
    [Fact]
    public void The_log_folders_refusal_fits_its_reason_the_same_way()
    {
        const string logs=@"C:\ProgramData\DeadWare\logs";
        Assert.Equal($"{logs} can be changed by {UsersSid}, so programs without administrator rights can change it and the lighting helper running as administrator keeps no log. Give only administrators write access to it, {OrRemove}",
            ProtectedFolders.LogRefusal(DeadWareDir,logs+@"\"+UserSid,Reason(logs,PathKind.Directory,rules:[new(UsersSid,0x2,true,false)])));
        Assert.Equal($"DeadWare can't check {logs}, so the lighting helper running as administrator keeps no log: {logs} is a link or junction.{Inside}",
            ProtectedFolders.LogRefusal(DeadWareDir,logs+@"\"+UserSid,Reason(logs,PathKind.Directory,link:true)));
    }

    // Ruling AM: only an elevated host refuses an OpenRGB folder that programs without administrator rights can change.
    [Fact]
    public void Only_an_elevated_host_checks_the_openrgb_folder()
    {
        Assert.Null(ProtectedFolders.OpenRgbFolderCheck(false));
        var check=ProtectedFolders.OpenRgbFolderCheck(true);
        Assert.NotNull(check);
        var dir=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try{Assert.False(check(Path.Combine(dir,"OpenRGB.exe")).Protected);}
        finally{Directory.Delete(dir,true);}
    }
}
