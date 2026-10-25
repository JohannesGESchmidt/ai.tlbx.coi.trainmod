using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Animations;
using Mafi.Core.Entities.Static;
using Mafi.Core.Products;
using Mafi.Core.Trains;
using TrainParameterMod;

internal static class MovementAndCargoChecks
{
    private static readonly string Owner = "TrainParameterMod.Tests.PhysicsEnvironment";
    private delegate Fix64 PhysicsStep(Train train, RelTile1f target, out Fix64 acceleration);

    internal static void Run(Action<bool, string> check)
    {
        var patches = new Harmony(TrainParameterMod.TrainParameterMod.Owner);
        var environment = new Harmony(Owner);
        patches.PatchAll(typeof(TrainPatches).Assembly);
        environment.Patch(AccessTools.Method(typeof(Train), "updateForcesBasedOnCurrentSpeed"), prefix: new HarmonyMethod(typeof(MovementAndCargoChecks), nameof(Forces)));
        environment.Patch(AccessTools.Method(typeof(Train), "estimateFreeRailInFront"), prefix: new HarmonyMethod(typeof(MovementAndCargoChecks), nameof(FreeRail)));
        environment.Patch(AccessTools.Method(typeof(TrainStationModule), "canReceive"), prefix: new HarmonyMethod(typeof(MovementAndCargoChecks), nameof(CanReceive)));
        try
        {
            CheckMovement(check);
            CheckCargo(check);
            CheckPooledCargo(check);
        }
        finally
        {
            patches.UnpatchAll(TrainParameterMod.TrainParameterMod.Owner);
            environment.UnpatchAll(Owner);
            Settings(1, 1, 1);
        }
    }

    private static void CheckMovement(Action<bool, string> check)
    {
        var train = Empty<Train>();
        var data = Empty<TrainStaticData>();
        var nativeBrakeForce = Fix32.One * 50;
        Set(data, "MaxBrakingForceKn", nativeBrakeForce);
        Set(train, "<Data>k__BackingField", data);
        Set(train, "<MassTons>k__BackingField", Fix32.One * 100);
        Set(train, "<ClearanceThrottle>k__BackingField", Percent.Hundred);
        Set(train, "<BrakingDistance>k__BackingField", Fix32.One.Tiles());
        Set(train, "<SpeedLimit>k__BackingField", Fix32.One.Tiles());
        Set(train, "<SpeedLimitAhead>k__BackingField", Fix32.One.Tiles());
        var start = Fix64.One / 10;
        Set(train, "m_speed", start);
        Set(train, "<Speed>k__BackingField", start.ToFix32().Tiles());
        Set(train, "<TargetSpeed>k__BackingField", Fix32.One.Tiles());
        var step = AccessTools.MethodDelegate<PhysicsStep>(AccessTools.Method(typeof(Train), "updateTrainMovementPhysics"));
        Settings(1, 1, 1);
        TrainPatches.RefreshBraking(train);
        var vanilla = step(train, Fix32.One.Tiles(), out var vanillaAcceleration);
        var vanillaDistance = train.BrakingDistance;
        check(vanilla > start, "actual native movement method produces positive acceleration on powered clear track");
        Settings(10, 1, 1);
        TrainPatches.RefreshBraking(train);
        var fast = step(train, Fix32.One.Tiles(), out var fastAcceleration);
        check(Near(fast - start, (vanilla - start) * 10), "10x acceleration changes the actual next-step speed gain by 10x");
        check(fastAcceleration == fast - start, "reported acceleration matches the modified movement step");
        Settings(0.1, 1, 1);
        TrainPatches.RefreshBraking(train);
        var slow = step(train, Fix32.One.Tiles(), out _);
        check(Near(slow - start, (vanilla - start) / 10), "0.1x acceleration reduces the actual next-step speed gain tenfold");
        Settings(1, 1, 1);
        TrainPatches.RefreshBraking(train);
        var braking = step(train, (Fix32.One / 20).Tiles(), out _);
        Settings(10, 1, 1);
        TrainPatches.RefreshBraking(train);
        var fastBraking = step(train, (Fix32.One / 20).Tiles(), out _);
        check(Near(start - fastBraking, (start - braking) * 10), "10x acceleration also multiplies actual native braking deceleration tenfold");
        check(train.BrakingDistance < vanillaDistance, "10x brake force immediately shortens the native predicted braking distance");
        TrainPatches.RefreshBraking(train);
        check(data.MaxBrakingForceKn == nativeBrakeForce * 10, "repeated/shared-data refresh cannot compound the brake multiplier");
        Settings(0.1, 1, 1);
        TrainPatches.RefreshBraking(train);
        var gentleBraking = step(train, (Fix32.One / 20).Tiles(), out _);
        check(Near(start - gentleBraking, (start - braking) / 10), "0.1x brake factor reduces actual native braking tenfold");
        check(train.BrakingDistance > vanillaDistance, "0.1x brake force lengthens the native braking distance for anticipation");
        check(step(train, RelTile1f.Zero, out _) >= Fix64.Zero, "native braking at a stop target cannot produce negative speed");
        Settings(10, 1, 1);
        TrainPatches.RefreshBraking(train);
        var target = (Fix32.One / 10 + Fix32.One / 100).Tiles();
        check(step(train, target, out _) <= target.Value.ToFix64(), "accelerated movement cannot overshoot the native target speed");
        Settings(1, 1, 1);
        TrainPatches.RefreshBraking(train);
        check(data.MaxBrakingForceKn == nativeBrakeForce && train.BrakingDistance == vanillaDistance,
            "reset restores original brake force and native stopping distance");
        check(step(train, Fix32.One.Tiles(), out _) == vanilla, "reset restores the exact native next-step speed");
    }

    private static void CheckCargo(Action<bool, string> check)
    {
        var load1 = CargoDuration(true, 1, out var loadDisconnect1);
        var load10 = CargoDuration(true, 10, out var loadDisconnect10);
        var loadSlow = CargoDuration(true, 0.1, out var loadDisconnectSlow);
        check(load10 < load1 && load1 < loadSlow, $"native full loading cycle changes at 10x / 1x / 0.1x: {load10} / {load1} / {loadSlow} ticks");
        check(loadSlow >= load1 * 9 && load10 <= load1 / 10 + 1, "loading time changes approximately tenfold in both directions");
        check(loadDisconnect10 < loadDisconnect1 && loadDisconnect1 < loadDisconnectSlow, "disconnect animation and end-of-cycle wait follow loading speed");
        var unload1 = CargoDuration(false, 1, out var unloadDisconnect1);
        var unload10 = CargoDuration(false, 10, out var unloadDisconnect10);
        var unloadSlow = CargoDuration(false, 0.1, out var unloadDisconnectSlow);
        check(unload10 < unload1 && unload1 < unloadSlow, $"native full unloading cycle changes at 10x / 1x / 0.1x: {unload10} / {unload1} / {unloadSlow} ticks");
        check(unloadSlow >= unload1 * 9 && unload10 <= unload1 / 10 + 1, "unloading time changes approximately tenfold in both directions");
        check(unloadDisconnect10 < unloadDisconnect1 && unloadDisconnect1 < unloadDisconnectSlow, "disconnect animation and end-of-cycle wait follow unloading speed");

        var clock = new CargoCyclePatches.Clock();
        var ticks = 0;
        for (var i = 0; i < 100; i++) ticks += clock.Advance(0.1);
        check(ticks == 10, "fractional cargo clock retains sub-tick progress without rounding drift");
        check(clock.Advance(10) == 10, "changing cargo speed does not retain stale fractional credit");
        var molten1 = MoltenDepartureDuration(1);
        var molten10 = MoltenDepartureDuration(10);
        var moltenSlow = MoltenDepartureDuration(0.1);
        check(molten10 < molten1 && molten1 < moltenSlow,
            $"native molten-wagon upright animation scales at 10x / 1x / 0.1x: {molten10} / {molten1} / {moltenSlow} ticks");
    }

    private static void CheckPooledCargo(Action<bool, string> check)
    {
        foreach (var count in new[] { 2, 4 })
        foreach (var loading in new[] { true, false })
        foreach (var rate in new[] { 0.1, 1, 10 })
        {
            Settings(1, rate, rate);
            var product = Empty<LooseProductProto>();
            var wagonProto = Empty<CargoWagonProto>();
            Set(wagonProto, "<Capacity>k__BackingField", (100 * count).Quantity());
            Set(wagonProto, "<SubCarCapacity>k__BackingField", 100.Quantity());
            Set(wagonProto, "ProductType", product.Type);
            var wagon = Empty<CargoWagon>();
            Set(wagon, "<Prototype>k__BackingField", wagonProto);
            typeof(TrainCarBase).GetField("<Prototype>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wagon, wagonProto);
            var stations = new TrainStationModule[count];
            var cars = new CargoWagon.SubCargoWagon[count];
            var buffers = new ProductBuffer[count];
            var entries = new Lyst<TrainStationGroup.StationAndDirection>();
            var manager = Empty<TrainStationManager>();
            var groups = new Dict<ITrainStationBase, TrainStationGroup>();
            var group = Empty<TrainStationGroup>();
            Set(manager, "m_trainStationEntities", groups);
            Set(group, "m_forwardEdgeStationEntities", entries);
            Set(group, "m_reverseEdgeStationEntities", new Lyst<TrainStationGroup.StationAndDirection>());
            for (var i = 0; i < count; i++)
            {
                var proto = Empty<TrainStationModuleProto>();
                Set(proto, "TransferPeriod", 20.Ticks());
                Set(proto, "ConnectionCompletionPerStepWhenLoading", 10.Percent());
                Set(proto, "ConnectionCompletionPerStepWhenUnloading", 10.Percent());
                Set(proto, "Graphics", Empty<TrainStationModuleProto.Gfx>());
                var station = stations[i] = Empty<TrainStationModule>();
                Set(station, "m_id", new EntityId(i + 1));
                Set(station, "m_proto", proto);
                Set(station, "<IsEnabled>k__BackingField", true);
                Set(station, "<IsForLoading>k__BackingField", loading);
                Set(station, "<TransferQuantity>k__BackingField", 5.Quantity().AsPartial);
                Set(station, "m_consumedPower", true);
                Set(station, "m_stationManager", manager);
                // All supply/free space starts at one end, the other modules cannot serve alone.
                var capacity = i == 0 ? 100 * count : 100;
                var buffer = buffers[i] = new ProductBuffer(capacity.Quantity(), product);
                if (loading ? i == 0 : i != 0) buffer.StoreAsMuchAs(capacity.Quantity());
                Set(station, "m_buffer", Option<ProductBuffer>.Create(buffer));
                entries.Add(new TrainStationGroup.StationAndDirection(station, TrainTrackTrajectoryDirection.Forward));
                groups.Add(station, group);
                cars[i] = new CargoWagon.SubCargoWagon(wagon, i, new ProductQuantity(product, (loading ? 0 : 100).Quantity()), default);
                cars[i].SetAlignedStation(Option<ITrainStationModule>.Create(station));
            }
            wagon.SubCars = new ImmutableArray<TrainCarBase.TrainSubCarBase>(cars);
            if (count == 4 && loading && rate == 1)
            {
                var target = stations[3];
                Set(stations[1], "<IsEnabled>k__BackingField", false);
                StationCargoPool.Prepare(target);
                check(buffers[3].Quantity.IsZero, "pool does not cross a disabled module");
                Set(stations[1], "<IsEnabled>k__BackingField", true);
                Set(stations[1], "<IsBalancingDisabled>k__BackingField", true);
                StationCargoPool.Prepare(target);
                check(buffers[3].Quantity.IsZero, "pool does not cross a module with balancing disabled");
                Set(stations[1], "<IsBalancingDisabled>k__BackingField", false);
                Set(stations[1], "<IsForLoading>k__BackingField", false);
                StationCargoPool.Prepare(target);
                check(buffers[3].Quantity.IsZero, "loading and unloading modules have separate pools");
                Set(stations[1], "<IsForLoading>k__BackingField", true);
                var otherProduct = Empty<LooseProductProto>();
                Set(otherProduct, "<Id>k__BackingField", new Mafi.Core.Prototypes.Proto.ID("OtherProduct"));
                Set(stations[1], "m_buffer", Option<ProductBuffer>.Create(new ProductBuffer(100.Quantity(), otherProduct)));
                StationCargoPool.Prepare(target);
                check(buffers[3].Quantity.IsZero, "pool does not cross a different product");
                Set(stations[1], "m_buffer", Option<ProductBuffer>.Create(buffers[1]));
                entries[2] = new TrainStationGroup.StationAndDirection(stations[2], TrainTrackTrajectoryDirection.Backward);
                StationCargoPool.Prepare(target);
                check(buffers[3].Quantity.IsZero, "opposite station sides have separate pools");
                entries[2] = new TrainStationGroup.StationAndDirection(stations[2], TrainTrackTrajectoryDirection.Forward);
                Set(group, "m_forwardEdgeStationEntities", new Lyst<TrainStationGroup.StationAndDirection>());
                Set(group, "m_reverseEdgeStationEntities", entries);
                target.RequestWagonLoad(cars[3], _ => true, out var active);
                check(active && buffers[3].Quantity.IsPositive,
                    "native request starts from an empty local buffer using the reverse-edge shared pool");
                check(AccessTools.Field(typeof(TrainStationModule), "m_wagonRequest").GetValue(target) != null,
                    "pooled request creates an actual native wagon-service request");
                Set(group, "m_forwardEdgeStationEntities", entries);
                Set(group, "m_reverseEdgeStationEntities", new Lyst<TrainStationGroup.StationAndDirection>());
            }
            var equal = true;
            var ticks = 0;
            var before = Total();
            while ((loading ? !cars[0].IsFullForLoading : !cars[0].IsEmpty) && ticks < 10000)
            {
                for (var i = 0; i < count; i++)
                {
                    if (loading) stations[i].RequestWagonLoad(cars[i], _ => true, out _);
                    else stations[i].RequestWagonUnload(cars[i], _ => true, out _);
                    if (loading) CargoCyclePatches.AdvanceLoading(stations[i], wagon, i);
                    else CargoCyclePatches.AdvanceUnloading(stations[i], wagon, i);
                }
                for (var i = 1; i < count; i++) equal &= cars[i].Cargo.Quantity == cars[0].Cargo.Quantity;
                ticks++;
            }
            check(equal && ticks < 10000, $"{count} pooled modules {(loading ? "load" : "unload")} all wagon parts equally at {rate}x ({ticks} ticks)");
            check(Total() == before, "pooled native cargo cycles conserve all station and wagon stock");

            // With pooling disabled the empty/full target must stay isolated.
            var isolatedStation = loading ? stations[count - 1] : stations[0];
            var targetBuffer = loading ? buffers[count - 1] : buffers[0];
            if (loading) targetBuffer.RemoveAsMuchAs(targetBuffer.Quantity);
            else targetBuffer.StoreAsMuchAs(targetBuffer.UsableCapacity);
            if (loading) buffers[0].StoreAsMuchAs(100.Quantity());
            else buffers[count - 1].RemoveAsMuchAs(100.Quantity());
            Set(isolatedStation, "<IsBalancingDisabled>k__BackingField", true);
            var isolated = targetBuffer.Quantity;
            StationCargoPool.Prepare(isolatedStation);
            check(targetBuffer.Quantity == isolated, "explicit balancing disable is preserved");

            Quantity Total()
            {
                var result = Quantity.Zero;
                for (var i = 0; i < count; i++) result += buffers[i].Quantity + cars[i].Cargo.Quantity;
                return result;
            }
        }
    }

    private static int CargoDuration(bool loading, double rate, out int disconnectTicks)
    {
        Settings(1, loading ? rate : 1, loading ? 1 : rate);
        var product = Empty<LooseProductProto>();
        var wagonProto = Empty<CargoWagonProto>();
        Set(wagonProto, "<Capacity>k__BackingField", 100.Quantity());
        Set(wagonProto, "<SubCarCapacity>k__BackingField", 100.Quantity());
        Set(wagonProto, "ProductType", product.Type);
        var wagon = Empty<CargoWagon>();
        Set(wagon, "<Prototype>k__BackingField", wagonProto);
        typeof(TrainCarBase).GetField("<Prototype>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wagon, wagonProto);
        var car = new CargoWagon.SubCargoWagon(wagon, 0, new ProductQuantity(product, (loading ? 0 : 100).Quantity()), default);
        wagon.SubCars = new ImmutableArray<TrainCarBase.TrainSubCarBase>(new TrainCarBase.TrainSubCarBase[] { car });
        var proto = Empty<TrainStationModuleProto>();
        Set(proto, "TransferPeriod", 20.Ticks());
        Set(proto, "ConnectionCompletionPerStepWhenLoading", 10.Percent());
        Set(proto, "ConnectionCompletionPerStepWhenUnloading", 10.Percent());
        var gfx = Empty<TrainStationModuleProto.Gfx>();
        Set(gfx, "AnimateConnectBeforeLoad", true);
        Set(gfx, "AnimateConnectBeforeUnload", true);
        Set(gfx, "AnimateLoadUnload", true);
        Set(proto, "Graphics", gfx);
        var station = Empty<TrainStationModule>();
        Set(station, "m_proto", proto);
        Set(station, "<IsForLoading>k__BackingField", loading);
        Set(station, "<TransferQuantity>k__BackingField", 5.Quantity().AsPartial);
        Set(station, "m_consumedPower", true); // Model a powered station; no electrical system is started.
        var buffer = new ProductBuffer(100.Quantity(), product);
        if (loading) buffer.StoreAsMuchAs(100.Quantity());
        Set(station, "m_buffer", Option<ProductBuffer>.Create(buffer));
        car.SetAlignedStation(Option<ITrainStationModule>.Create(station));
        var ticks = 0;
        while ((loading ? car.Cargo.Quantity.Value < 100 : car.Cargo.Quantity.IsPositive) && ticks < 10000)
        {
            if (loading) CargoCyclePatches.AdvanceLoading(station, wagon, 0);
            else CargoCyclePatches.AdvanceUnloading(station, wagon, 0);
            ticks++;
        }
        if (ticks == 10000) throw new InvalidOperationException("Native cargo cycle did not complete.");
        if (car.Cargo.Quantity + buffer.Quantity != 100.Quantity()) throw new InvalidOperationException("Cargo was lost or manufactured.");
        disconnectTicks = 0;
        while (!station.CanReleaseWagon && disconnectTicks < 10000)
        {
            CargoCyclePatches.AdvanceDisconnect(station);
            disconnectTicks++;
        }
        if (disconnectTicks == 10000) throw new InvalidOperationException("Native disconnect did not complete.");

        // Exercise the actual station animation method. No renderer or Unity window is created.
        var provider = Empty<AnimationStatesProvider>();
        Set(provider, "m_animationStatesImpl", ImmutableArray<IAnimationStateImpl>.Empty);
        Set(station, "<AnimationStatesProvider>k__BackingField", provider);
        AccessTools.MethodDelegate<Action<TrainStationModule>>(AccessTools.Method(typeof(TrainStationModule), "updateAnimation"))(station);
        var visualSpeed = (Percent)typeof(AnimationStatesProvider).GetField("m_utilization", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(provider)!;
        var expected = TrainSettings.Factor(loading ? rate : -rate);
        if (visualSpeed != expected) throw new InvalidOperationException("Native animation speed did not follow the cargo clock.");
        return ticks;
    }

    private static int MoltenDepartureDuration(double rate)
    {
        Settings(1, 1, rate);
        var station = Empty<TrainStationModule>();
        var proto = Empty<CargoWagonMoltenProto>();
        Set(proto, nameof(CargoWagonMoltenProto.LadleAngleSpeedPerTick), AngleDegrees1f.OneDegree);
        var wagon = Empty<CargoWagonMolten>();
        Set(wagon, "<Prototype>k__BackingField", proto);
        var car = Empty<CargoWagonMolten.SubCargoWagonMolten>();
        Set(car, "MoltenWagon", wagon);
        Set(car, "<LadleAngle>k__BackingField", AngleDegrees1f.Deg90);
        var ticks = 0;
        while (!car.IsReadyForDeparture(station) && ticks < 10000) ticks++;
        if (ticks == 10000) throw new InvalidOperationException("Native molten-wagon upright animation did not finish.");
        return ticks + 1;
    }

    // Stub world/environment queries, not the native movement or cargo algorithms under test.
    private static bool Forces(Train __instance)
    {
        Set(__instance, "<AccelerationForceKnAvailable>k__BackingField", Fix32.One * 100);
        Set(__instance, "<AirDragKn>k__BackingField", Fix32.Zero);
        Set(__instance, "<GradeForceKn>k__BackingField", Fix32.Zero);
        Set(__instance, "<RollDragKn>k__BackingField", Fix32.Zero);
        return false;
    }
    private static bool FreeRail(ref RelTile1f __result) { __result = (Fix32.One * 100).Tiles(); return false; }
    private static bool CanReceive(ref bool __result) { __result = true; return false; }
    private static bool Near(Fix64 lhs, Fix64 rhs) => (lhs - rhs).Abs() <= Fix64.One / 100000;
    private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void Settings(double acceleration, double loading, double unloading) => typeof(TrainParameterMod.TrainParameterMod)
        .GetField("settings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new TrainSettings(1, acceleration, 1, 1, loading, unloading));
    private static void Set(object target, string name, object value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) { field.SetValue(target, value); return; }
        }
        throw new MissingFieldException(target.GetType().Name, name);
    }
}
