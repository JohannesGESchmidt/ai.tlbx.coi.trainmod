using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Core.Entities;
using Mafi.Core.Trains;

namespace TrainParameterMod;

/// <summary>Advance native cargo work in logical substeps, including its animation phases.</summary>
internal static class CargoCyclePatches
{
    private static readonly MethodInfo LoadMethod = AccessTools.Method(typeof(TrainStationModule), "handleLoad");
    private static readonly MethodInfo UnloadMethod = AccessTools.Method(typeof(TrainStationModule), "handleUnload");
    private static readonly MethodInfo DisconnectMethod = AccessTools.Method(typeof(TrainStationModule), "disconnectFromWagon");
    private static readonly Action<TrainStationModule, CargoWagon, int> Load = AccessTools.MethodDelegate<Action<TrainStationModule, CargoWagon, int>>(LoadMethod);
    private static readonly Action<TrainStationModule, CargoWagon, int> Unload = AccessTools.MethodDelegate<Action<TrainStationModule, CargoWagon, int>>(UnloadMethod);
    private static readonly Action<TrainStationModule> Disconnect = AccessTools.MethodDelegate<Action<TrainStationModule>>(DisconnectMethod);
    private static readonly ConditionalWeakTable<TrainStationModule, Clock> Clocks = new ConditionalWeakTable<TrainStationModule, Clock>();
    [ThreadStatic] private static int nativeWorkDepth;

    internal sealed class Clock
    {
        public Clock() { }
        private double credit;
        private double lastRate;
        internal int Advance(double rate)
        {
            if (lastRate != rate) { credit = 0; lastRate = rate; }
            credit += rate;
            var steps = (int)Math.Floor(credit + 1e-9);
            credit = Math.Max(0, credit - steps);
            return steps;
        }
    }

    private static double Rate(ITrainStationModule station) => station.IsForLoading
        ? TrainParameterMod.Settings.Loading : TrainParameterMod.Settings.Unloading;

    internal static void AdvanceLoading(TrainStationModule station, CargoWagon wagon, int index) => AdvanceCargo(station, wagon, index, Load);
    internal static void AdvanceUnloading(TrainStationModule station, CargoWagon wagon, int index) => AdvanceCargo(station, wagon, index, Unload);

    private static void AdvanceCargo(TrainStationModule station, CargoWagon wagon, int index,
        Action<TrainStationModule, CargoWagon, int> nativeStep)
    {
        var steps = Clocks.GetOrCreateValue(station).Advance(Rate(station));
        if (steps == 0)
        {
            // The native station still has scheduled work, even on a fractional-clock tick.
            // Preserve its train's wait while retaining normal empty/full supply behavior.
            var buffer = station.Buffer.ValueOrNull;
            var car = wagon.GetSubCargoWagon(index);
            if (buffer != null && (station.IsForLoading
                ? buffer.Quantity.IsPositive && !car.IsFullForLoading && car.CanReceive(buffer.Product)
                : buffer.UsableCapacity.IsPositive && !car.IsEmpty))
                wagon.Train.ValueOrNull?.NotifyTransfersActive();
            return;
        }
        nativeWorkDepth++;
        try
        {
            for (var i = 0; i < steps; i++) nativeStep(station, wagon, index);
        }
        finally { nativeWorkDepth--; }
    }

    internal static void AdvanceDisconnect(TrainStationModule station)
    {
        var steps = Clocks.GetOrCreateValue(station).Advance(Rate(station));
        for (var i = 0; i < steps; i++) Disconnect(station);
    }

    private static void StepAnimation(AnimationStatesProvider provider, Percent speed, Percent progress, TrainStationModule station) =>
        provider.Step(speed * TrainSettings.Factor(Rate(station)), progress);

    private static AngleDegrees1f LadleStep(AngleDegrees1f step, ITrainStationModule station) => nativeWorkDepth > 0
        ? step : step * TrainSettings.Factor(Rate(station)).ToFix32();

    [HarmonyPatch(typeof(TrainStationModule), "SimUpdateInternal")]
    private static class StationWork
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var load = false;
            var unload = false;
            var disconnect = false;
            foreach (var instruction in instructions)
            {
                string? replacement = null;
                if (instruction.Calls(LoadMethod)) { replacement = nameof(AdvanceLoading); load = true; }
                if (instruction.Calls(UnloadMethod)) { replacement = nameof(AdvanceUnloading); unload = true; }
                if (instruction.Calls(DisconnectMethod)) { replacement = nameof(AdvanceDisconnect); disconnect = true; }
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(CargoCyclePatches), replacement);
                }
                yield return instruction;
            }
            if (!load || !unload || !disconnect) throw new InvalidOperationException("Native cargo-cycle call sites changed; refusing an incomplete timing patch.");
        }
    }

    [HarmonyPatch(typeof(TrainStationModule), "updateAnimation")]
    private static class StationAnimation
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var step = AccessTools.Method(typeof(AnimationStatesProvider), nameof(AnimationStatesProvider.Step));
            var found = false;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(step))
                {
                    found = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(CargoCyclePatches), nameof(StepAnimation));
                }
                yield return instruction;
            }
            if (!found) throw new InvalidOperationException("Native station animation call site changed.");
        }
    }

    [HarmonyPatch]
    private static class MoltenWagonAnimation
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CargoWagonMolten.SubCargoWagonMolten), nameof(CargoWagonMolten.SubCargoWagonMolten.IsReadyForUnloading));
            yield return AccessTools.Method(typeof(CargoWagonMolten.SubCargoWagonMolten), nameof(CargoWagonMolten.SubCargoWagonMolten.IsReadyForDeparture));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(CargoWagonMoltenProto), nameof(CargoWagonMoltenProto.LadleAngleSpeedPerTick));
            var found = false;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.LoadsField(field))
                {
                    found = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CargoCyclePatches), nameof(LadleStep)));
                }
            }
            if (!found) throw new InvalidOperationException("Native molten-wagon animation step changed.");
        }
    }
}
