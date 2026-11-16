using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class WindowsStorageTests
{
    [Theory][InlineData("0 C: D:","0")][InlineData("2","2")][InlineData("_Total",null)][InlineData("10 E:","10")]
    public void Instance_names_map_to_drive_index(string instance,string? expected)=>Assert.Equal(expected,WindowsStorage.InstanceDriveIndex(instance));
    [Fact] public void Parse_builds_drives_with_ids_and_volumes(){ var drives=WindowsStorage.Parse([new("S1","NVMe A",0,"NVMe",1000,false,[("C","OS",1000,400)]),new(null,"USB",1,"USB",64,true,[("E","",64,10)])]);
        Assert.Equal("S1",drives[0].Id);Assert.True(drives[0].Persistable);Assert.Equal("USB#1",drives[1].Id);Assert.False(drives[1].Persistable);Assert.Equal(400,drives[0].Volumes[0].FreeBytes);
        Assert.Equal("",drives[0].IdentityNote);Assert.Equal("no serial number",drives[1].IdentityNote);
        Assert.Equal("removable drive",WindowsStorage.Parse([new("R1","Stick",2,"USB",64,true,[])])[0].IdentityNote);}
    private static readonly WmiDisk Disk=new("S1","NVMe A",0,"NVMe",1000,false,[]);
    [Fact] public void Enumeration_failure_keeps_the_list_and_does_not_retry_before_60_seconds()
    {
        int calls=0;bool fail=false;
        var storage=new WindowsStorage(()=>{calls++;if(fail)throw new InvalidOperationException();return [Disk];},()=>new());
        Assert.Single(storage.Drives(0));Assert.Equal(1,calls);
        fail=true;
        Assert.Single(storage.Drives(60));Assert.Equal(2,calls);
        Assert.Single(storage.Drives(61));Assert.Single(storage.Drives(119));Assert.Equal(2,calls);
        Assert.Single(storage.Drives(120));Assert.Equal(3,calls);
    }
    [Fact] public void Counter_error_appears_only_after_60_seconds_of_failure_and_opens_are_throttled()
    {
        int opens=0;
        var storage=new WindowsStorage(()=>[Disk],()=>{opens++;throw new InvalidOperationException();});
        foreach(var (t,expectError) in new[]{(0d,false),(1d,false),(59d,false),(60d,true)})
        {
            var values=new Dictionary<string,double?>();
            var drives=storage.Drives(t);storage.Read(drives,values,out var error);
            Assert.Null(values["storage/S1/read"]);
            if(expectError)Assert.Contains("Disk counters unavailable",error);else Assert.Null(error);
        }
        Assert.Equal(2,opens);
    }
    [Fact] public void Counters_publish_activity_as_clamped_inverse_of_idle()
    {
        var storage=new WindowsStorage(()=>[Disk],()=>new(){["0"]=new(()=>100,()=>50,()=>105)});
        var values=new Dictionary<string,double?>();storage.Read(storage.Drives(0),values,out var error);
        Assert.Null(values["storage/S1/read"]);
        values.Clear();storage.Read(storage.Drives(0.5),values,out error);
        Assert.Null(error);Assert.Equal(100,values["storage/S1/read"]);Assert.Equal(50,values["storage/S1/write"]);Assert.Equal(0,values["storage/S1/active"]);
    }

    [Fact] public void Counters_survive_reenumeration_with_the_same_indexes_and_reopen_when_they_change()
    {
        int opens=0;IReadOnlyList<WmiDisk> disks=[Disk];
        var storage=new WindowsStorage(()=>disks,()=>{opens++;return new(){["0"]=new(()=>100,()=>50,()=>90),["1"]=new(()=>7,()=>8,()=>90)};});
        double?[] Poll(double t){var v=new Dictionary<string,double?>();storage.Read(storage.Drives(t),v,out _);return [v["storage/S1/read"]];}
        Assert.Null(Poll(0)[0]);Assert.Equal(100,Poll(0.5)[0]);
        Assert.Equal(100,Poll(60)[0]);Assert.Equal(100,Poll(60.5)[0]);Assert.Equal(1,opens);
        disks=[Disk,new("S2","SATA B",1,"SATA",2000,false,[])];
        Assert.Null(Poll(120)[0]);Assert.Equal(2,opens);Assert.Equal(100,Poll(120.5)[0]);Assert.Equal(2,opens);
    }

    private static readonly WmiDisk Second=new("S2","SATA B",1,"SATA",2000,false,[]);
    /// <summary>A counter set that counts its collections; it can watch the disks, report them changed, or fail to collect.</summary>
    private sealed class CountingSet:DiskCounterSet
    {
        public int Collects;
        public bool Watching,Change,Fail;
        public override bool WatchesDrives=>Watching;
        public override void Collect() {Collects++;if(Fail)throw new InvalidOperationException();DrivesChanged=Change;}
    }
    private static Dictionary<string,double?> Poll(WindowsStorage storage,double t)
    {
        var values=new Dictionary<string,double?>();storage.Read(storage.Drives(t),values,out _);return values;
    }

    [Fact] public void One_collection_a_poll_serves_every_drive()
    {
        var set=new CountingSet{["0"]=new(()=>1,()=>2,()=>50),["1"]=new(()=>3,()=>4,()=>100)};
        var storage=new WindowsStorage(()=>[Disk,Second],()=>set);
        // The poll that opens the set publishes nothing and does not collect again: opening primed the rates.
        Assert.Null(Poll(storage,0)["storage/S1/read"]);Assert.Equal(0,set.Collects);
        var values=Poll(storage,0.5);
        Assert.Equal(1,set.Collects);
        Assert.Equal(1,values["storage/S1/read"]);Assert.Equal(2,values["storage/S1/write"]);Assert.Equal(50,values["storage/S1/active"]);
        Assert.Equal(3,values["storage/S2/read"]);Assert.Equal(4,values["storage/S2/write"]);Assert.Equal(0,values["storage/S2/active"]);
        Poll(storage,1);Assert.Equal(2,set.Collects);
    }

    [Fact] public void A_disk_without_a_reading_is_null_and_the_others_still_publish()
    {
        var storage=new WindowsStorage(()=>[Disk,Second],()=>new(){["0"]=new(()=>double.NaN,()=>double.NaN,()=>double.NaN),["1"]=new(()=>3,()=>4,()=>90)});
        Poll(storage,0);
        var values=new Dictionary<string,double?>();storage.Read(storage.Drives(0.5),values,out var error);
        Assert.Null(error);
        Assert.Null(values["storage/S1/read"]);Assert.Null(values["storage/S1/write"]);Assert.Null(values["storage/S1/active"]);
        Assert.Equal(3,values["storage/S2/read"]);Assert.Equal(10,values["storage/S2/active"]);
    }

    [Fact] public void A_failed_collection_nulls_every_drive_and_the_set_is_opened_again_at_most_every_60_seconds()
    {
        int opens=0;
        var storage=new WindowsStorage(()=>[Disk,Second],()=>{opens++;return new CountingSet{Fail=true,["0"]=new(()=>1,()=>2,()=>3),["1"]=new(()=>1,()=>2,()=>3)};});
        Poll(storage,0);
        var failed=Poll(storage,0.5);
        Assert.Null(failed["storage/S1/read"]);Assert.Null(failed["storage/S2/read"]);
        Poll(storage,30);Assert.Equal(1,opens);
        Poll(storage,60);Assert.Equal(2,opens);
    }

    [Fact] public void Watched_disks_enumerate_every_ten_minutes_and_after_a_change_once_it_settles_then_once_more()
    {
        int calls=0;var set=new CountingSet{Watching=true,["0"]=new(()=>1,()=>2,()=>3)};
        var storage=new WindowsStorage(()=>{calls++;return [Disk];},()=>set);
        Poll(storage,0);Poll(storage,0.5);
        Poll(storage,60);Poll(storage,599);Assert.Equal(1,calls);
        // A drive appears: the counters see it on this poll's collection.
        set.Change=true;Poll(storage,100);set.Change=false;
        Poll(storage,101);Assert.Equal(1,calls);
        Poll(storage,102);Assert.Equal(2,calls);
        Poll(storage,111);Assert.Equal(2,calls);
        Poll(storage,112);Assert.Equal(3,calls);
        Poll(storage,200);Assert.Equal(3,calls);
        Poll(storage,712);Assert.Equal(4,calls);
    }

    [Fact] public void A_change_that_keeps_changing_waits_until_it_settles()
    {
        int calls=0;var set=new CountingSet{Watching=true,["0"]=new(()=>1,()=>2,()=>3)};
        var storage=new WindowsStorage(()=>{calls++;return [Disk];},()=>set);
        Poll(storage,0);
        // A new disk appears, then gets its letters a poll later: two changes, one enumeration.
        set.Change=true;Poll(storage,10);Poll(storage,11);set.Change=false;
        Poll(storage,12);Assert.Equal(1,calls);
        Poll(storage,13);Assert.Equal(2,calls);
    }

    [Fact] public void Without_watched_disks_enumeration_stays_every_60_seconds()
    {
        int calls=0;
        var storage=new WindowsStorage(()=>{calls++;return [Disk];},()=>new CountingSet{["0"]=new(()=>1,()=>2,()=>3)});
        Poll(storage,0);Poll(storage,0.5);Poll(storage,59);Assert.Equal(1,calls);
        Poll(storage,60);Assert.Equal(2,calls);
    }

    [Fact] public void A_later_enumeration_runs_off_the_poll_and_its_drives_arrive_on_a_later_poll()
    {
        var queued=new List<Action>();IReadOnlyList<WmiDisk> disks=[Disk];int calls=0;
        var storage=new WindowsStorage(()=>{calls++;return disks;},()=>new DiskCounterSet(),work=>queued.Add(work));
        // The first enumeration runs on the poll, so drives show from the first sample.
        Assert.Single(storage.Drives(0));Assert.Empty(queued);Assert.Equal(1,calls);
        disks=[Disk,Second];
        Assert.Single(storage.Drives(60));Assert.Single(queued);Assert.Equal(1,calls);
        // One at a time: while it is in flight no other starts, and the poll keeps the drives it had.
        Assert.Single(storage.Drives(120));Assert.Single(queued);
        queued[0]();
        Assert.Equal(2,storage.Drives(120.5).Count);Assert.Equal(2,calls);
    }

    [Fact] public void Free_space_is_read_every_ten_seconds_and_keeps_the_drive_list_current()
    {
        var reads=new List<string>();long freeC=400;
        var storage=new WindowsStorage(()=>[new("S1","NVMe A",0,"NVMe",1000,false,[("C","OS",1000,400)])],()=>new DiskCounterSet(),freeSpace:letter=>{reads.Add(letter);return freeC;});
        Assert.Equal(400,Poll(storage,0)["storage/S1/volume/C/free"]);Assert.Single(reads);
        freeC=300;
        // Between reads the last figure is published again.
        Assert.Equal(400,Poll(storage,5)["storage/S1/volume/C/free"]);Assert.Single(reads);
        Assert.Equal(300,Poll(storage,10)["storage/S1/volume/C/free"]);Assert.Equal(2,reads.Count);
        // The Storage page's free space comes from the drive list, which no longer waits for the next enumeration.
        Assert.Equal(300,storage.Drives(10.5)[0].Volumes[0].FreeBytes);
    }

    [Fact] public void An_unknown_free_space_keeps_the_enumerated_figure_in_the_drive_list()
    {
        var storage=new WindowsStorage(()=>[new("S1","NVMe A",0,"NVMe",1000,false,[("C","OS",1000,400)])],()=>new DiskCounterSet(),freeSpace:_=>null);
        Assert.Null(Poll(storage,0)["storage/S1/volume/C/free"]);
        Assert.Equal(400,storage.Drives(0.5)[0].Volumes[0].FreeBytes);
    }

    [Fact] public void The_real_disk_counters_read_every_disk_from_one_query()
    {
        PdhDiskCounterSet set;
        // Performance counters can be disabled on a machine; the storage page then says so, and there is nothing to check here.
        try {set=new PdhDiskCounterSet();}
        catch(InvalidOperationException) {return;}
        using(set)
        {
            Thread.Sleep(100);set.Collect();
            Thread.Sleep(250);set.Collect();
            Assert.True(set.WatchesDrives);Assert.False(set.DrivesChanged);
            // Every Windows machine has at least one physical disk; "_Total" is not one.
            Assert.NotEmpty(set);
            foreach(var (index,counters) in set)
            {
                Assert.True(index.All(char.IsAsciiDigit),index);
                double read=counters.Read(),write=counters.Write(),idle=counters.Idle();
                Assert.True(double.IsNaN(read)||read>=0,$"read {read}");Assert.True(double.IsNaN(write)||write>=0,$"write {write}");
                Assert.True(double.IsNaN(idle)||idle is >=0 and <=100,$"idle {idle}");
            }
        }
        Assert.Throws<ObjectDisposedException>(set.Collect);
    }
}
