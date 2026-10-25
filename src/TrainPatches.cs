using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mafi;
using Mafi.Core.Trains;

namespace TrainParameterMod;

/// <summary>Small runtime hooks; no game DLLs or decompiled implementations are distributed.</summary>
internal static class TrainPatches
{
    private static readonly Action<Train, RelTile1f> SetMaxSpeed =
        AccessTools.MethodDelegate<Action<Train, RelTile1f>>(AccessTools.PropertySetter(typeof(Train), nameof(Train.MaxSpeed)));
    private static readonly Action<Train, RelTile1f> SetTargetSpeed =
        AccessTools.MethodDelegate<Action<Train, RelTile1f>>(AccessTools.PropertySetter(typeof(Train), nameof(Train.TargetSpeed)));
    private static readonly Action<Train, RelTile1f> SetBrakingDistance =
        AccessTools.MethodDelegate<Action<Train, RelTile1f>>(AccessTools.PropertySetter(typeof(Train), nameof(Train.BrakingDistance)));
    private static readonly ConditionalWeakTable<TrainStaticData, BrakingBaseline> BrakingBaselines = new ConditionalWeakTable<TrainStaticData, BrakingBaseline>();

    private sealed class BrakingBaseline
    {
        internal readonly Fix32 Force;
        internal BrakingBaseline(TrainStaticData data) { Force = data.MaxBrakingForceKn; }
    }

    internal static void RefreshBraking(Train train)
    {
        var data = train.Data;
        var baseline = BrakingBaselines.GetValue(data, value => new BrakingBaseline(value));
        var force = baseline.Force.ScaledBy(TrainSettings.Factor(TrainParameterMod.Settings.Acceleration));
        if (data.MaxBrakingForceKn != force)
        {
            // All native braking, curve/station anticipation and stopping-distance calculations
            // read this same field. Scale it once per shared data object, from its original value.
            AccessTools.Field(typeof(TrainStaticData), nameof(TrainStaticData.MaxBrakingForceKn)).SetValue(data, force);
            AccessTools.Field(typeof(TrainStaticData), "m_forcesAtSpeeds").SetValue(data, default(Option<TrainForcesAtSpeeds>));
            AccessTools.PropertySetter(typeof(TrainStaticData), nameof(TrainStaticData.DataVersion))
                .Invoke(data, new object[] { data.DataVersion + 1 });
        }
        if (!train.Speed.IsPositive)
            SetBrakingDistance(train, RelTile1f.Zero);
        else if (train.MassTons.IsPositive && force.IsPositive)
            SetBrakingDistance(train, data.ComputeStoppingDistanceEstimate(train.Speed, train.MassTons, Fix32.Zero));
    }

    internal static void RefreshTopSpeed(Train train)
    {
        RefreshBraking(train);
        // Always derive from the game's unscaled static data, never from a previously scaled value.
        var baseline = train.IsReversed ? train.Data.MaxBackwardSpeedCombined : train.Data.MaxForwardsSpeedCombined;
        SetMaxSpeed(train, baseline.ScaledBy(TrainSettings.Factor(TrainParameterMod.Settings.Speed)));
    }

    [HarmonyPatch(typeof(Train), "updateCachedValues")]
    private static class TopSpeed
    {
        private static void Postfix(Train __instance) => RefreshTopSpeed(__instance);
    }

    [HarmonyPatch(typeof(Train), nameof(Train.SetTargetSpeed))]
    private static class ManualTargetSpeed
    {
        private static void Postfix(Train __instance, RelTile1f targetSpeed) =>
            SetTargetSpeed(__instance, targetSpeed.Min(__instance.MaxSpeed));
    }

    [HarmonyPatch(typeof(EntityWithTrainTrackBaseProto), nameof(EntityWithTrainTrackBaseProto.MaxSpeedTilesPerTick), MethodType.Getter)]
    private static class TrackSpeed
    {
        private static void Postfix(EntityWithTrainTrackBaseProto __instance, ref RelTile1f __result)
        {
            // Preserve low-speed station and depot maneuvering limits.
            if (!(__instance is TrainTrackProto)) return;
            var config = TrainParameterMod.Settings;
            if (!__instance.IsStraight)
                __result = config.CurveSlowdown == 0
                    ? Train.MAX_SPEED
                    : __result.ScaledBy(TrainSettings.Factor(Math.Min(100, 1 / config.CurveSlowdown))).Min(Train.MAX_SPEED);
            __result = __result.ScaledBy(TrainSettings.Factor(config.Speed));
        }
    }

    [HarmonyPatch(typeof(Train), "updateTrainMovementPhysics")]
    private static class Acceleration
    {
        private static void Postfix(Train __instance, RelTile1f targetSpeed, Fix64 ___m_speed,
            ref Fix64 acceleration, ref Fix64 __result)
        {
            var multiplier = TrainParameterMod.Settings.Acceleration;
            // Patch the final movement result, not a small helper the runtime can inline.
            // Never amplify coasting, braking or a stop imposed by reservations/signals.
            if (multiplier == 1 || __result <= ___m_speed || !__instance.AccelerationForceKnCurrent.IsPositive) return;
            var increase = (__result - ___m_speed).ScaledBy(TrainSettings.Factor(multiplier));
            __result = (___m_speed + increase).Min(targetSpeed.Value.ToFix64());
            acceleration = __result - ___m_speed;
        }
    }
}
