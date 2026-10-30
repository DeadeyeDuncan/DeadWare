namespace DeadWare.App;

// One direction's visual activity, separate from the reported traffic/rates.
internal sealed class ConnectionMotion(bool active)
{
    public bool Active { get; set; } = active;
    public double Level { get; private set; } = active ? 1 : 0;
    public double Time { get; private set; }
    public bool Moving => Active || Level > 0;

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        double target = Active ? 1 : 0, tau = Active ? .15 : .4;
        double decay = Math.Exp(-seconds / tau);
        // Integrate the easing speed so stopping/restarting never resets the wave phase.
        Time += target * seconds + (Level - target) * tau * (1 - decay);
        Level = target + (Level - target) * decay;
        if (Math.Abs(Level - target) < .002) Level = target;
    }
}
