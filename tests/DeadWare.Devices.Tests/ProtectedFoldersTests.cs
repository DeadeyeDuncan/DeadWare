using System.Security.AccessControl;
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
