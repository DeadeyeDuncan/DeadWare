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

    // Polish item 7 (B4/B13/B18): when %ProgramData%\DeadWare itself fails the check, the refusal names that folder, not openrgb beneath it,
    // gives InstallLocation's reason, and points the remedy at it: deleting openrgb alone would change nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refused_DeadWare_folder_is_named_with_its_reason_and_remedy(bool openRgbExists)
    {
        var root=Path.Combine(Path.GetTempPath(),"dw-pf-"+Guid.NewGuid().ToString("N"));
        string programData=Path.Combine(root,"pd"),deadWare=Path.Combine(programData,"DeadWare");
        // Made without administrator rights, so owned by this user and open to programs running as them.
        Directory.CreateDirectory(openRgbExists?Path.Combine(deadWare,"openrgb"):deadWare);
        try
        {
            var refusal=Assert.Throws<UnprotectedFolderException>(()=>ProtectedFolders.OpenRgbConfigDirectory(true,programData,Path.Combine(root,"local")));
            Assert.NotNull(refusal.Reason);
            Assert.StartsWith(deadWare+" ",refusal.Reason);
            Assert.StartsWith($"{deadWare} isn't restricted to administrators (",refusal.Message);
            Assert.Contains(refusal.Reason!.TrimEnd('.'),refusal.Message);
            Assert.EndsWith("Remove that folder, or give only administrators write access to it, then turn lighting off and on.",refusal.Message);
            Assert.DoesNotContain(Path.Combine(deadWare,"openrgb"),refusal.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(deadWare,refusal.Folder);
        }
        finally{Directory.Delete(root,true);}
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
