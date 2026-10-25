using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Mods;

namespace TrainParameterMod;

/// <summary>An immutable snapshot shared by the simulation and path-finding hooks.</summary>
internal sealed class TrainSettings
{
    internal static readonly TrainSettings Vanilla = new TrainSettings(1, 1, 1, 1, 1, 1);

    internal readonly double CurveSlowdown;
    internal readonly double Acceleration;
    internal readonly double Capacity;
    internal readonly double Speed;
    internal readonly double Loading;
    internal readonly double Unloading;

    internal TrainSettings(double curve, double acceleration, double capacity, double speed, double loading, double unloading)
    {
        CurveSlowdown = Validate(curve, 0, 5);
        Acceleration = Validate(acceleration, 0.1, 10);
        Capacity = Validate(capacity, 0.1, 10);
        Speed = Validate(speed, 0.25, 5);
        Loading = Validate(loading, 0.1, 10);
        Unloading = Validate(unloading, 0.1, 10);
    }

    internal static TrainSettings Read(ModJsonConfig config) => new TrainSettings(
        config.GetDouble("curve_slowdown_factor", 1),
        config.GetDouble("acceleration_multiplier", 1),
        config.GetDouble("wagon_capacity_multiplier", 1),
        config.GetDouble("speed_multiplier", 1),
        config.GetDouble("loading_speed_multiplier", 1),
        config.GetDouble("unloading_speed_multiplier", 1));

    // The property system takes a delta, whereas ScaledBy takes an absolute factor.
    internal static Percent Factor(double value) => Percent.FromRatio((int)Math.Round(value * 10000), 10000);
    internal Percent CapacityDelta => Factor(Capacity - 1);

    internal Dict<string, object> ToValues() => new Dict<string, object>
    {
        { "curve_slowdown_factor", CurveSlowdown },
        { "acceleration_multiplier", Acceleration },
        { "wagon_capacity_multiplier", Capacity },
        { "speed_multiplier", Speed },
        { "loading_speed_multiplier", Loading },
        { "unloading_speed_multiplier", Unloading }
    };

    private static double Validate(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value), "Train setting is outside its supported range.");
        return value;
    }
}
