using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Mafi;
using Mafi.Base.Prototypes.Trains;
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
        Set(data, "MaxBrakingForceKn", Fix32.One * 50);
        Set(train, "<Data>k__BackingField", data);
        Set(train, "<MassTons>k__BackingField", Fix32.One * 100);
        Set(train, "<ClearanceThrottle>k__BackingField", Percent.Hundred);
        Set(train, "<BrakingDistance>k__BackingField", Fix32.One.Tiles());
        Set(train, "<SpeedLimit>k__BackingField", Fix32.One.Tiles());
        Set(train, "<SpeedLimitAhead>k__BackingField", Fix32.One.Tiles());
        var start = Fix64.One / 10;
        Set(train, "m_speed", start);
        Set(train, "<TargetSpeed>k__BackingField", Fix32.One.Tiles());
        var step = AccessTools.MethodDelegate<PhysicsStep>(AccessTools.Method(typeof(Train), "updateTrainMovementPhysics"));
        Settings(1, 1, 1);
        var vanilla = step(train, Fix32.One.Tiles(), out var vanillaAcceleration);
        check(vanilla > start, "actual native movement method produces positive acceleration on powered clear track");
        Settings(10, 1, 1);
        var fast = step(train, Fix32.One.Tiles(), out var fastAcceleration);
        check(Near(fast - start, (vanilla - start) * 10), "10x acceleration changes the actual next-step speed gain by 10x");
        check(fastAcceleration == fast - start, "reported acceleration matches the modified movement step");
        Settings(0.1, 1, 1);
        var slow = step(train, Fix32.One.Tiles(), out _);
        check(Near(slow - start, (vanilla - start) / 10), "0.1x acceleration reduces the actual next-step speed gain tenfold");
        Settings(1, 1, 1);
        var braking = step(train, (Fix32.One / 20).Tiles(), out _);
        Settings(10, 1, 1);
        check(step(train, (Fix32.One / 20).Tiles(), out _) == braking, "actual native braking result is unchanged at 10x acceleration");
        var target = (Fix32.One / 10 + Fix32.One / 10000).Tiles();
        check(step(train, target, out _) <= target.Value.ToFix64(), "accelerated movement cannot overshoot the native target speed");
        Settings(1, 1, 1);
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
