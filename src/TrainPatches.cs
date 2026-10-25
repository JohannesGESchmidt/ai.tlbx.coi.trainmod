using System;
using HarmonyLib;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Core.Trains;

namespace TrainParameterMod;

/// <summary>Small runtime hooks; no game DLLs or decompiled implementations are distributed.</summary>
internal static class TrainPatches
{
    private static readonly Action<Train, RelTile1f> SetMaxSpeed =
        AccessTools.MethodDelegate<Action<Train, RelTile1f>>(AccessTools.PropertySetter(typeof(Train), nameof(Train.MaxSpeed)));
    private static readonly Action<Train, RelTile1f> SetTargetSpeed =
        AccessTools.MethodDelegate<Action<Train, RelTile1f>>(AccessTools.PropertySetter(typeof(Train), nameof(Train.TargetSpeed)));

    internal static void RefreshTopSpeed(Train train)
    {
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

    [HarmonyPatch(typeof(TrainStaticData), nameof(TrainStaticData.ScaleAccelerationBasedOnFunFactor))]
    private static class Acceleration
    {
        private static void Postfix(ref Fix64 __result)
        {
            if (__result.IsPositive)
                __result = __result.ScaledBy(TrainSettings.Factor(TrainParameterMod.Settings.Acceleration));
        }
    }

    [HarmonyPatch(typeof(TrainStationModule), nameof(TrainStationModule.TransferQuantity), MethodType.Getter)]
    private static class CargoTransfer
    {
        private static void Postfix(TrainStationModule __instance, ref PartialQuantity __result)
        {
            var config = TrainParameterMod.Settings;
            __result = __result.ScaledBy(TrainSettings.Factor(__instance.IsForLoading ? config.Loading : config.Unloading));
        }
    }
}
