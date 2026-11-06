using System;

namespace TGDeblocker.App.Rendering;

// Semi-implicit Euler, 1/240-second substeps as in the upstream spring driver.
internal sealed class GlassSpring
{
    public double Value, Velocity, Target;
    public double Response, Damping;
    public GlassSpring(double response, double damping) { Response = response; Damping = damping; }
    public void Snap(double value) { Value = Target = value; Velocity = 0; }
    public bool Step(double dt, double tolerance = .04, double speedTolerance = .4)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Min(dt, .05) * 240));
        double h = Math.Min(dt, .05) / steps, omega = 2 * Math.PI / Response;
        for (int i = 0; i < steps; i++)
        {
            Velocity += (-omega * omega * (Value - Target) - 2 * Damping * omega * Velocity) * h;
            Value += Velocity * h;
        }
        if (Math.Abs(Value - Target) < tolerance && Math.Abs(Velocity) < speedTolerance)
        { Snap(Target); return false; }
        return true;
    }
}
