namespace TGDeblocker.Mobile.Rendering;

// Semi-implicit Euler with bounded 1/240s substeps, adapted from the app's desktop renderer.
internal sealed class GlassSpring(double response, double damping)
{
    public double Value, Velocity, Target;
    public double Response = response, Damping = damping;
    public void Snap(double value) { Value = Target = value; Velocity = 0; }
    public bool Step(double dt)
    {
        dt = Math.Clamp(dt, 0, .05);
        var steps = Math.Max(1, (int)Math.Ceiling(dt * 240));
        var h = dt / steps;
        var omega = 2 * Math.PI / Response;
        for (var i = 0; i < steps; i++)
        {
            Velocity += (-omega * omega * (Value - Target) - 2 * Damping * omega * Velocity) * h;
            Value += Velocity * h;
        }
        if (Math.Abs(Value - Target) < .0003 && Math.Abs(Velocity) < .003) { Snap(Target); return false; }
        return true;
    }
}
