namespace DeadWare.Core;
public static class SensorValidity
{
    public static double? Normalize(double? value,bool requiresLowLevel,bool lowLevelAvailable)=>requiresLowLevel&&!lowLevelAvailable?null:SampleHistory.Finite(value);
}
