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
            check(Harmony.GetAllPatchedMethods().CountByOwner() == 6, "actual mod EarlyInit installs all hooks before native load callbacks");
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
    }

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
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

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
