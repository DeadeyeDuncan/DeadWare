using DeadWare.Devices;
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
}
