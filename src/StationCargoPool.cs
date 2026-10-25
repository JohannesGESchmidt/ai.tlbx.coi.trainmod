using HarmonyLib;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core.Entities.Static;
using Mafi.Core.Trains;

namespace TrainParameterMod;

/// <summary>Supply native cargo batches from the connected station's compatible buffers.</summary>
internal static class StationCargoPool
{
    private static readonly AccessTools.FieldRef<TrainStationModule, TrainStationManager> Manager =
        AccessTools.FieldRefAccess<TrainStationModule, TrainStationManager>("m_stationManager");
    private static readonly AccessTools.FieldRef<TrainStationModule, Option<ProductBuffer>> Buffer =
        AccessTools.FieldRefAccess<TrainStationModule, Option<ProductBuffer>>("m_buffer");

    internal static void Prepare(TrainStationModule station)
    {
        var local = Buffer(station).ValueOrNull;
        if (local == null || station.IsNotEnabled || station.IsBalancingDisabled) return;
        // Keep fractional transfer carry available, too. Never move more than a local batch needs.
        var batch = station.TransferQuantity.IntegerPart + 1.Quantity();
        if ((station.IsForLoading ? local.Quantity : local.UsableCapacity) >= batch) return;
        var manager = Manager(station);
        if (manager == null || !manager.TrainStationEntities.TryGetValue(station, out var group)) return;
        if (!PrepareInList(group.ForwardEdgeStationEntities, station, local, batch))
            PrepareInList(group.ReverseEdgeStationEntities, station, local, batch);
    }

    private static bool PrepareInList(IIndexable<TrainStationGroup.StationAndDirection> entries,
        TrainStationModule station, ProductBuffer local, Quantity batch)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (!ReferenceEquals(entries[index].Station, station)) continue;
            for (var direction = -1; direction <= 1; direction += 2)
            {
                for (var i = index + direction; i >= 0 && i < entries.Count; i += direction)
                {
                    var entry = entries[i];
                    if (!(entry.Station is TrainStationModule other) || other.IsNotEnabled || other.IsBalancingDisabled
                        || other.IsForLoading != station.IsForLoading || Side(entry) != Side(entries[index])) break;
                    var buffer = Buffer(other).ValueOrNull;
                    if (buffer == null || buffer.Product != local.Product) break;
                    var needed = batch - (station.IsForLoading ? local.Quantity : local.UsableCapacity);
                    if (needed.IsNotPositive) return true;
                    Move(station.IsForLoading ? buffer : local, station.IsForLoading ? local : buffer, needed);
                }
            }
            return true;
        }
        return false;
    }

    private static int Side(TrainStationGroup.StationAndDirection entry) =>
        (entry.Direction == TrainTrackTrajectoryDirection.Forward ? 0 : 1) ^ (entry.Station.Transform.IsReflected ? 1 : 0);

    private static void Move(ProductBuffer source, ProductBuffer target, Quantity needed)
    {
        var moved = source.RemoveAsMuchAs_DoNotReport(needed.Min(source.Quantity).Min(target.UsableCapacity));
        var remainder = target.StoreAsMuchAs_DoNotReport(moved);
        if (remainder.IsPositive) source.StoreAsMuchAs_DoNotReport(remainder);
    }

    // Native requests reject an empty/full local buffer before a cargo cycle even starts.
    [HarmonyPatch(typeof(TrainStationModule), nameof(TrainStationModule.RequestWagonLoad))]
    private static class LoadingRequest
    {
        private static void Prefix(TrainStationModule __instance) => Prepare(__instance);
    }

    [HarmonyPatch(typeof(TrainStationModule), nameof(TrainStationModule.RequestWagonUnload))]
    private static class UnloadingRequest
    {
        private static void Prefix(TrainStationModule __instance) => Prepare(__instance);
    }
}
