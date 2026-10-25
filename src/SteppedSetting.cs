using System;
using Mafi.Core.Mods;

namespace TrainParameterMod;

/// <summary>Maps native integer slider detents to exact decimal tenths in the draft config.</summary>
internal sealed class SteppedSetting
{
    private readonly ModJsonConfig draft;
    internal readonly ModJsonConfigParam Parameter;
    internal readonly int Minimum;
    internal readonly int Maximum;
    internal int Tick { get; private set; }
    internal double Value => Tick / 10.0;

    internal SteppedSetting(ModJsonConfig draft, ModJsonConfigParam parameter)
    {
        this.draft = draft;
        Parameter = parameter;
        Minimum = (int)Math.Ceiling(parameter.Min!.Value * 10);
        Maximum = (int)Math.Floor(parameter.Max!.Value * 10);
        Refresh();
    }

    internal void Refresh() => SetTick((int)Math.Round(draft.GetDouble(Parameter.Name) * 10, MidpointRounding.AwayFromZero));

    internal void SetTick(int tick)
    {
        Tick = Math.Max(Minimum, Math.Min(Maximum, tick));
        if (!draft.TrySetValue(Parameter.Name, Value, out var error))
            throw new InvalidOperationException(error);
    }
}
