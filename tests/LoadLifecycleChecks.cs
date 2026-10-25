using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.GameLoop;
using Mafi.Core.Mods;
using Mafi.Core.PropertiesDb;
using Mafi.Core.Simulation;
using Mafi.Core.Trains;
using TrainParameterMod;

internal static class LoadLifecycleChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var properties = Empty<PropsDb>();
        var table = new Dict<string, IProperty>();
        Set(properties, "m_properties", table);
        var trains = Empty<TrainsManager>();
        Set(trains, "m_trains", new Lyst<Train>());
        var sim = DispatchProxy.Create<ISimLoopEvents, SimRecorder>();
        var simRecorder = (SimRecorder)(object)sim;
        var loop = DispatchProxy.Create<IGameLoopEvents, LoopRecorder>();
        var loopRecorder = (LoopRecorder)(object)loop;
        var mod = CreateMod(root);
        try
        {
            // An older save's property table lacks the newly registered property until native
            // PropsDb.initSelf (InitAfterLoad) repairs it. EarlyInit must tolerate this exact state.
            var originalLookupFails = false;
            try { properties.GetProperty(IdsCore.PropertyIds.TrainsCapacityMultiplier); }
            catch (KeyNotFoundException) { originalLookupFails = true; }
            check(originalLookupFails, "older-save fixture reproduces the native missing-capacity-property exception");
            ((IMod)mod).EarlyInit(null!);
            check(table.Count == 0, "saved-game EarlyInit tolerates a missing capacity property without modifying the table");
            check(Harmony.GetAllPatchedMethods().CountByOwner() == 11, "actual mod EarlyInit installs all hooks before native load callbacks");
            check(loopRecorder.RendererRegistrations == 0, "UI initialization is deferred until the game has finished loading");

            // Represent the native InitAfterLoad repair, before GameBuilder dispatches Initialize.
            var capacity = new PropertyPercentMult(IdsCore.PropertyIds.TrainsCapacityMultiplier.Value, Percent.Hundred, new Notifications());
            table.Add(capacity.Id, capacity);
            var mapping = mod.GetType().GetInterfaceMap(typeof(IMod));
            var initializeIndex = Array.FindIndex(mapping.InterfaceMethods, m => m.Name == "Initialize");
            check(mapping.TargetMethods[initializeIndex].DeclaringType == mod.GetType(), "GameBuilder's IMod.Initialize dispatch reaches the mod instead of DataOnlyMod's empty method");
            // DependencyResolverBuilder requires a running game. Supply native objects plus loop stubs
            // to the same late-init body, without bypassing the game's integrity/runtime checks.
            mod.InitializeGameState(properties, trains, sim, loop, null!);
            check(capacity.Value == TrainSettings.Factor(2), "loaded-save Initialize applies restored capacity only after property repair");
            check(TrainParameterMod.TrainParameterMod.Settings.Speed == 2, "resuming automatically publishes saved settings in the late init phase");
            check(loopRecorder.RendererRegistrations == 1, "loaded game schedules live menu initialization after state is ready");
            mod.QueueSettings(TrainSettings.Vanilla);
            simRecorder.BeforeCommands.Fire();
            check(capacity.Value == Percent.Hundred, "real registered pre-command callback applies live changes after load");
        }
        finally { mod.Dispose(); }

        table.Clear();
        var failedMod = CreateMod(root);
        try
        {
            ((IMod)failedMod).EarlyInit(null!);
            try
            {
                // If the game really cannot supply the property, cleanup must preserve the original error.
                failedMod.InitializeGameState(properties, trains, sim, loop, null!);
                check(false, "missing native property must fail explicitly");
            }
            catch (KeyNotFoundException ex)
            {
                check(ex.Message.Contains("TrainsCapacityMultiplier"), "failed initialization preserves the original missing-property exception");
                check(Harmony.GetAllPatchedMethods().CountByOwner() == 0, "failed initialization removes every installed hook");
                check(TrainParameterMod.TrainParameterMod.Settings.Capacity == 1, "failed initialization resets the active settings");
            }
        }
        finally { failedMod.Dispose(); }

        CheckAddedToExistingSave(root, check);
    }

    private static void CheckAddedToExistingSave(string root, Action<bool, string> check)
    {
        using var manifestJson = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "manifest.json")));
        check(manifestJson.RootElement.GetProperty("can_add_to_saved_game").GetBoolean(), "distributed manifest permits adding the mod to an existing save");
        var manifest = new ModManifest(root, "TrainParameterMod", default, "Train Parameter Mod",
            ImmutableArray<string>.Empty, canAddToSavedGame: true);
        var mod = new TrainParameterMod.TrainParameterMod(manifest);
        var worldConfig = new ExistingWorldConfig();
        check(mod.JsonConfig.IsEmpty, "newly added mod starts without any saved parameters");
        var configs = AttachNativeConfig(mod, new ImmutableArray<Mafi.Core.Game.IConfig>(new Mafi.Core.Game.IConfig[] { worldConfig }));
        check(mod.JsonConfig.Parameters.Count == 6, "native game config attachment supplies all six parameters to a previously unmodded save");
        var defaults = TrainSettings.Read(mod.JsonConfig);
        check(defaults.CurveSlowdown == 1 && defaults.Acceleration == 1 && defaults.Capacity == 1 && defaults.Speed == 1 &&
            defaults.Loading == 1 && defaults.Unloading == 1, "absence of saved mod values uses vanilla defaults for every parameter");
        check(configs.Length == 2 && ReferenceEquals(configs[0], worldConfig) && ReferenceEquals(configs[1], mod.JsonConfig),
            "native attachment preserves existing world config and registers the new mod config for saving");

        var properties = Empty<PropsDb>();
        var table = new Dict<string, IProperty>();
        Set(properties, "m_properties", table);
        var trainData = Empty<TrainStaticData>();
        Set(trainData, nameof(TrainStaticData.MaxForwardsSpeedCombined), (Fix32.One / 10).Tiles());
        Set(trainData, nameof(TrainStaticData.MaxBackwardSpeedCombined), (Fix32.One / 20).Tiles());
        var train = Empty<Train>();
        Set(train, "<Data>k__BackingField", trainData);
        Set(train, "<IsReversed>k__BackingField", true);
        Set(train, "<LifetimeDistanceTraveled>k__BackingField", Fix64.One * 123);
        var trains = Empty<TrainsManager>();
        var trainList = new Lyst<Train>();
        trainList.Add(train);
        Set(trains, "m_trains", trainList);
        var sim = DispatchProxy.Create<ISimLoopEvents, SimRecorder>();
        var events = (SimRecorder)(object)sim;
        var loop = DispatchProxy.Create<IGameLoopEvents, LoopRecorder>();
        try
        {
            ((IMod)mod).EarlyInit(null!);
            check(table.Count == 0, "first add also tolerates the old save's incomplete property table during EarlyInit");
            var capacity = new PropertyPercentMult(IdsCore.PropertyIds.TrainsCapacityMultiplier.Value, Percent.Hundred, new Notifications());
            capacity.AddOrSetModifier("existing-difficulty", TrainSettings.Factor(0.5), default);
            table.Add(capacity.Id, capacity);
            mod.InitializeGameState(properties, trains, sim, loop, null!);
            check(capacity.Value == TrainSettings.Factor(1.5), "first-add default capacity preserves existing difficulty or research modifiers");
            check(train.MaxSpeed == trainData.MaxBackwardSpeedCombined, "first add initializes an existing reversed train at its native speed");

            mod.QueueSettings(new TrainSettings(0.5, 2, 2, 3, 4, 5));
            events.BeforeCommands.Fire();
            check(capacity.Value == TrainSettings.Factor(3) && train.MaxSpeed == trainData.MaxBackwardSpeedCombined.ScaledBy(TrainSettings.Factor(3)),
                "first-add live edits update existing train and capacity without rebuilding the train");
            check(ReferenceEquals(train.Data, trainData) && train.IsReversed && train.LifetimeDistanceTraveled == Fix64.One * 123,
                "adding the mod and applying settings preserves train data identity, direction and lifetime distance");

            // The returned configs contain the same live config instance the game saves. Feed this
            // saved-value container back into the actual native schema-attachment path on reload.
            mod.Dispose();
            var resumed = new TrainParameterMod.TrainParameterMod(manifest);
            try
            {
                var restoredConfigs = AttachNativeConfig(resumed, configs);
                var saved = TrainSettings.Read(resumed.JsonConfig);
                check(saved.CurveSlowdown == 0.5 && saved.Acceleration == 2 && saved.Capacity == 2 && saved.Speed == 3 &&
                    saved.Loading == 4 && saved.Unloading == 5, "next load restores all six values through native config attachment after first add");
                check(ReferenceEquals(restoredConfigs[0], worldConfig), "next load still retains the pre-existing world config");
                ((IMod)resumed).EarlyInit(null!);
                resumed.InitializeGameState(properties, trains, sim, loop, null!);
                check(capacity.Value == TrainSettings.Factor(3), "resuming replaces the saved owned capacity modifier without compounding bonuses");
                check(train.MaxSpeed == trainData.MaxBackwardSpeedCombined.ScaledBy(TrainSettings.Factor(3)),
                    "resuming reapplies the saved speed multiplier once to the existing train");
            }
            finally { resumed.Dispose(); }
        }
        finally { mod.Dispose(); }
    }

    private static ImmutableArray<Mafi.Core.Game.IConfig> AttachNativeConfig(TrainParameterMod.TrainParameterMod mod,
        ImmutableArray<Mafi.Core.Game.IConfig> savedConfigs)
    {
        var loaded = new LoadedModData(mod.Manifest, Option<Type>.Create(mod.GetType()), ImmutableArray<Assembly>.Empty);
        var builderType = typeof(IMod).Assembly.GetType("Mafi.Core.Game.GameBuilder", throwOnError: true)!;
        var attach = builderType.GetMethod("AttachModJsonConfigs", BindingFlags.Static | BindingFlags.Public)!;
        return (ImmutableArray<Mafi.Core.Game.IConfig>)attach.Invoke(null, new object[]
        {
            new ImmutableArray<LoadedModData>(new[] { loaded }),
            new ImmutableArray<IMod>(new IMod[] { mod }), savedConfigs
        })!;
    }

    private sealed class ExistingWorldConfig : Mafi.Core.Game.IConfig { }

    private static TrainParameterMod.TrainParameterMod CreateMod(string root)
    {
        var mod = new TrainParameterMod.TrainParameterMod(new ModManifest(root, "TrainParameterMod", default,
            "Train Parameter Mod", ImmutableArray<string>.Empty));
        ModJsonConfig.TryLoadFromFile(System.IO.Path.Combine(root, "config.json"), "TrainParameterMod", out var schema, out _);
        schema.TrySetValue("wagon_capacity_multiplier", 2.0, out _);
        schema.TrySetValue("speed_multiplier", 2.0, out _);
        typeof(DataOnlyMod).GetField("<JsonConfig>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mod, schema);
        return mod;
    }

    private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(target, value);

    private static int CountByOwner(this IEnumerable<MethodBase> methods)
    {
        var count = 0;
        foreach (var method in methods)
            if (Harmony.GetPatchInfo(method)?.Owners.Contains(TrainParameterMod.TrainParameterMod.Owner) == true) count++;
        return count;
    }

    private sealed class Notifications : IPropertiesDbInternal
    {
        public void OnPropertyChanged(IProperty property, ImmutableArray<Type> types) { }
    }

    public class SimRecorder : DispatchProxy
    {
        internal readonly IEvent BeforeCommandsProxy = DispatchProxy.Create<IEvent, EventRecorder>();
        internal EventRecorder BeforeCommands => (EventRecorder)(object)BeforeCommandsProxy;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_UpdateBeforeCmdProc"
            ? BeforeCommandsProxy : throw new InvalidOperationException("Unexpected simulation call: " + method.Name);
    }

    public class EventRecorder : DispatchProxy
    {
        private Action? callback;
        internal void Fire() => callback?.Invoke();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "AddNonSaveable") callback += (Action)args![1]!;
            else if (method.Name == "RemoveNonSaveable") callback -= (Action)args![1]!;
            else throw new InvalidOperationException("Unexpected simulation-event call: " + method.Name);
            return null;
        }
    }

    public class LoopRecorder : DispatchProxy
    {
        internal int RendererRegistrations;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != "RegisterRendererInitState") throw new InvalidOperationException("Unexpected game-loop call: " + method.Name);
            RendererRegistrations++;
            return null;
        }
    }
}
