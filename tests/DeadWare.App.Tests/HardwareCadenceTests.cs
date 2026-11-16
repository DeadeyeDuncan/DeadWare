using System.Globalization;
using DeadWare.App;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace DeadWare.App.Tests;

public class HardwareCadenceTests
{
    private sealed class FakeSensor(IHardware hardware,string name,SensorType type,int index,float? value) : ISensor
    {
        public IControl Control=>null!;
        public IHardware Hardware=>hardware;
        // Built on every read, as LibreHardwareMonitor's sensor builds it.
        public Identifier Identifier=>new(hardware.Identifier,type.ToString().ToLowerInvariant(),index.ToString(CultureInfo.InvariantCulture));
        public int Index=>index;
        public bool IsDefaultHidden=>false;
        public float? Max=>null;
        public float? Min=>null;
        public string Name {get;set;}=name;
        public IReadOnlyList<IParameter> Parameters=>[];
        public SensorType SensorType=>type;
        public float? Value {get;set;}=value;
        public IEnumerable<SensorValue> Values=>[];
        public TimeSpan ValuesTimeWindow {get;set;}=TimeSpan.FromDays(1);
        public void ResetMin() {}
        public void ResetMax() {}
        public void ClearValues() {}
        public void Accept(IVisitor visitor) {}
        public void Traverse(IVisitor visitor) {}
    }
    /// <summary>Hardware that counts its updates and can fail them; its sensors are given as (name, type, value).</summary>
    private sealed class FakeHardware : IHardware
    {
        private readonly List<ISensor> sensors=[];
        private readonly IHardware[] sub;
        public int Updates;
        public bool Fail;
        public FakeHardware(string id,string name,HardwareType type,IHardware[]? sub=null,params (string Name,SensorType Type,float Value)[] readings)
        {
            Identifier=new(id.Trim('/').Split('/'));Name=name;HardwareType=type;this.sub=sub??[];
            for(int i=0;i<readings.Length;i++)sensors.Add(new FakeSensor(this,readings[i].Name,readings[i].Type,i,readings[i].Value));
        }
        public HardwareType HardwareType {get;}
        public Identifier Identifier {get;}
        public string Name {get;set;}
        public IHardware Parent=>null!;
        public ISensor[] Sensors=>sensors.ToArray();
        public IHardware[] SubHardware=>sub;
        public IDictionary<string,string> Properties {get;}=new Dictionary<string,string>();
        public string GetReport()=>"";
        public void Update() {Updates++;if(Fail)throw new InvalidOperationException("fake update");}
        public event SensorEventHandler? SensorAdded {add{} remove{}}
        public event SensorEventHandler? SensorRemoved {add{} remove{}}
        public void Accept(IVisitor visitor) {}
        public void Traverse(IVisitor visitor) {}
    }

    private sealed record Board(FakeHardware Cpu,FakeHardware Gpu,FakeHardware SuperIo,FakeHardware Motherboard,FakeHardware Memory,FakeHardware Cooler)
    {
        public IHardware[] All=>[Cpu,Gpu,Motherboard,Memory,Cooler];
    }
    private static Board Machine()
    {
        var superIo=new FakeHardware("/lpc/nct6798d/0","Nuvoton NCT6798D",HardwareType.SuperIO,null,("CPU Fan",SensorType.Fan,1200),("Pump Fan",SensorType.Fan,2400),("Vcore",SensorType.Voltage,1.2f),("System",SensorType.Temperature,35));
        return new(new("/amdcpu/0","AMD Ryzen 7 7700X",HardwareType.Cpu,null,("CPU Total",SensorType.Load,10),("Core (Tctl/Tdie)",SensorType.Temperature,50)),
            new("/gpu-nvidia/0","NVIDIA GeForce RTX 4070",HardwareType.GpuNvidia,null,("GPU Core",SensorType.Temperature,45),("GPU Fan",SensorType.Fan,1500)),
            superIo,new("/motherboard","ASUS PRIME",HardwareType.Motherboard,[superIo]),
            new("/ram","Total Memory",HardwareType.Memory,null,("Memory",SensorType.Load,40)),
            new("/nzxt/kraken/0","NZXT Kraken",HardwareType.Cooler,null,("Pump",SensorType.Fan,2800)));
    }
    private sealed record Poll(List<SensorReading> Sensors,List<SensorReading> Fresh,List<string> Errors);
    private static List<Poll> Run(Collector collector,IEnumerable<IHardware> hardware,int polls,double interval=0.5,double start=0)
    {
        var result=new List<Poll>();
        for(int n=0;n<polls;n++)
        {
            var poll=new Poll([],[],[]);
            foreach(var h in hardware)collector.ReadHardware(h,poll.Sensors,poll.Fresh,poll.Errors,start+n*interval,interval,null);
            result.Add(poll);
        }
        return result;
    }
    private static Collector NewCollector()=>new(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5),null);

    [Fact] public async Task Slow_hardware_updates_every_two_seconds_and_shows_its_last_rows_between()
    {
        var m=Machine();await using var collector=NewCollector();
        var polls=Run(collector,m.All,8);
        // CPU and GPU every poll; the board chip, memory and cooler at 0 s and 2 s.
        Assert.Equal(8,m.Cpu.Updates);Assert.Equal(8,m.Gpu.Updates);
        Assert.Equal(2,m.SuperIo.Updates);Assert.Equal(2,m.Motherboard.Updates);Assert.Equal(2,m.Memory.Updates);Assert.Equal(2,m.Cooler.Updates);
        // Every row is shown on every poll, in the same order; only the rows read on a poll enter the history.
        Assert.All(polls,p=>Assert.Equal(polls[0].Sensors.Select(s=>s.Id),p.Sensors.Select(s=>s.Id)));
        Assert.Equal(10,polls[0].Sensors.Count);
        Assert.Equal([10,4,4,4,10,4,4,4],polls.Select(p=>p.Fresh.Count));
        Assert.All(polls.Where((_,n)=>n%4!=0),p=>Assert.All(p.Fresh,s=>Assert.True(s.HardwareType is "Cpu" or "GpuNvidia")));
        // LibreHardwareMonitor's own value log is switched off on every sensor read.
        Assert.All(m.SuperIo.Sensors,s=>Assert.Equal(TimeSpan.Zero,s.ValuesTimeWindow));
    }

    [Fact] public async Task The_chip_with_the_pump_header_updates_every_poll_and_the_rest_stays_slow()
    {
        var m=Machine();await using var collector=NewCollector();
        Run(collector,m.All,1);
        // The pump comes from the board's SuperIO here, as CpuPumpSpeed chose it.
        var pump=new CpuPumpSpeed();
        Assert.Equal(2400d,pump.Read(Run(collector,m.All,1,start:0.5)[0].Sensors,1));
        Assert.Equal("/lpc/nct6798d/0",pump.HardwareId);
        collector.Cadence.PumpHardwareId=pump.HardwareId;
        int superIo=m.SuperIo.Updates,memory=m.Memory.Updates,board=m.Motherboard.Updates;
        Run(collector,m.All,8,start:1);
        Assert.Equal(superIo+8,m.SuperIo.Updates);
        // The motherboard's own update does nothing in LibreHardwareMonitor, so it stays slow while its SuperIO runs every poll.
        Assert.Equal(board+2,m.Motherboard.Updates);Assert.Equal(memory+2,m.Memory.Updates);
    }

    [Fact] public async Task A_pump_on_an_AIO_cooler_keeps_the_cooler_at_full_speed_and_lets_the_board_slow_down()
    {
        var m=Machine();await using var collector=NewCollector();
        collector.Cadence.PumpHardwareId="/nzxt/kraken/0";
        Run(collector,m.All,8);
        Assert.Equal(8,m.Cooler.Updates);Assert.Equal(2,m.SuperIo.Updates);
    }

    [Fact] public async Task A_failing_slow_chip_keeps_its_error_between_updates()
    {
        var m=Machine();m.Memory.Fail=true;await using var collector=NewCollector();
        var polls=Run(collector,m.All,4);
        Assert.Equal(1,m.Memory.Updates);
        Assert.All(polls,p=>Assert.Contains("Total Memory: InvalidOperationException",p.Errors));
    }

    [Theory][InlineData(0.5,4)][InlineData(1,2)][InlineData(2,1)][InlineData(5,1)]
    public void Slow_hardware_updates_every_two_seconds_or_every_poll_when_polling_is_slower(double interval,int pollsPerUpdate)
    {
        var cadence=new HardwareCadence();var updates=new List<int>();
        for(int n=0;n<4*pollsPerUpdate;n++)if(cadence.Due("/ram",HardwareType.Memory,n*interval,interval))updates.Add(n);
        Assert.Equal(Enumerable.Range(0,4).Select(k=>k*pollsPerUpdate),updates);
        Assert.True(cadence.Due("/amdcpu/0",HardwareType.Cpu,0.1,interval));Assert.True(cadence.Due("/amdcpu/0",HardwareType.Cpu,0.2,interval));
    }

    [Fact] public void A_slightly_early_poll_still_updates_slow_hardware_on_its_fourth_poll()
    {
        var cadence=new HardwareCadence();
        Assert.True(cadence.Due("/ram",HardwareType.Memory,0,0.5));
        Assert.False(cadence.Due("/ram",HardwareType.Memory,1.5,0.5));
        // Poll timing drifts by a few milliseconds: 1.98 s is the fourth poll, not a reason to wait for a fifth.
        Assert.True(cadence.Due("/ram",HardwareType.Memory,1.98,0.5));
    }
}
