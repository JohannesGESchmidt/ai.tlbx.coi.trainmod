using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Core.Mods;
using Mafi.Core.PropertiesDb;
using Mafi.Core.Trains;
using TrainParameterMod;

internal static class Program
{
    private static int checks;

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: SmokeTests <game root> <repo root>"); return 2; }
        var managed = Path.Combine(args[0], "Captain of Industry_Data", "Managed");
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var path = Path.Combine(managed, new AssemblyName(request.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Run(args[1]); Console.WriteLine($"PASS: {checks} integration checks against installed game assemblies."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string root)
    {
        Check(ModJsonConfig.TryLoadFromFile(Path.Combine(root, "config.json"), "TrainParameterMod", out var config, out var error), "native config schema parses: " + error);
        Check(config.Parameters.Count == 6, "six native settings");
        foreach (var key in config.Parameters.Keys) Check(config.GetDouble(key) == 1, key + " vanilla default");
        Check(config.TrySetValue("speed_multiplier", 2.0, out error), "native setting can be edited: " + error);
        Check(!config.TrySetValue("speed_multiplier", 100.0, out _), "native range validation");
        Check(TrainSettings.Read(config).Speed == 2, "read edited native settings");

        var harmony = new Harmony(TrainParameterMod.TrainParameterMod.Owner);
        SetSettings(TrainSettings.Vanilla);
        var data = Empty<TrainStaticData>();
        var acceleration = Fix64.One;
        var vanillaAcceleration = data.ScaleAccelerationBasedOnFunFactor(acceleration);
        // Patches can be installed without creating a Unity window or controlling the desktop.
        harmony.PatchAll(typeof(TrainPatches).Assembly);
        try
        {
            Check(Harmony.GetAllPatchedMethods().CountByOwner(TrainParameterMod.TrainParameterMod.Owner) == 11, "gameplay, cargo-cycle, pooled requests, animation and pause-menu targets patched");
            Check(data.ScaleAccelerationBasedOnFunFactor(acceleration) == vanillaAcceleration, "default acceleration unchanged");
            SetSettings(new TrainSettings(1, 2, 1, 1, 3, 4));
            Check(data.ScaleAccelerationBasedOnFunFactor(acceleration) == vanillaAcceleration, "small acceleration helper is left intact; final movement is patched instead");
            Check(data.ScaleAccelerationBasedOnFunFactor(-acceleration) == -vanillaAcceleration, "braking unchanged");

            var track = Empty<TrainTrackProto>();
            var trackLimit = (Fix32.One / 100).Tiles();
            SetField(track, typeof(EntityWithTrainTrackBaseProto), "<MaxSpeedTilesPerTick>k__BackingField", trackLimit);
            SetField(track, typeof(TrainTrackProto), "CurveRadius", (TrainTrackRadius)1);
            Check(ReadTrackSpeed(track) == trackLimit, "default curve limit");
            SetSettings(new TrainSettings(2, 1, 1, 1, 1, 1));
            Check(ReadTrackSpeed(track) == trackLimit.ScaledBy(TrainSettings.Factor(0.5)), "double slowdown halves curve limit");
            SetSettings(new TrainSettings(0.5, 1, 1, 1, 1, 1));
            Check(ReadTrackSpeed(track) == trackLimit.ScaledBy(TrainSettings.Factor(2)), "half slowdown doubles curve limit");
            SetSettings(new TrainSettings(0, 1, 1, 1, 1, 1));
            Check(ReadTrackSpeed(track) == Train.MAX_SPEED, "zero disables curve cap");
            SetSettings(new TrainSettings(double.Epsilon, 1, 1, 1, 1, 1));
            Check(ReadTrackSpeed(track) <= Train.MAX_SPEED, "near-zero curve factor cannot overflow");
            SetField(track, typeof(TrainTrackProto), "CurveRadius", TrainTrackRadius.Inf);
            SetSettings(new TrainSettings(5, 1, 1, 2, 1, 1));
            Check(ReadTrackSpeed(track) == trackLimit.ScaledBy(TrainSettings.Factor(2)), "straight rails ignore curve setting and scale speed");

            var train = Empty<Train>();
            SetField(data, typeof(TrainStaticData), nameof(TrainStaticData.MaxForwardsSpeedCombined), (Fix32.One * 2).Tiles());
            SetField(data, typeof(TrainStaticData), nameof(TrainStaticData.MaxBackwardSpeedCombined), Fix32.One.Tiles());
            SetField(train, typeof(Train), "<Data>k__BackingField", data);
            TrainPatches.RefreshTopSpeed(train);
            TrainPatches.RefreshTopSpeed(train);
            Check(train.MaxSpeed == 4.Tiles(), "top speed changes without compounding");
            train.SetTargetSpeed((Fix32.One * 3).Tiles());
            Check(train.TargetSpeed == 3.Tiles(), "manual driving uses modified top speed");
            train.SetTargetSpeed((Fix32.One * 8).Tiles());
            Check(train.TargetSpeed == 4.Tiles(), "manual driving remains capped");
            SetField(train, typeof(Train), "<IsReversed>k__BackingField", true);
            TrainPatches.RefreshTopSpeed(train);
            Check(train.MaxSpeed == 2.Tiles(), "reverse top speed scales independently");

            var station = Empty<TrainStationModule>();
            var batch = 10.Quantity().AsPartial;
            SetField(station, typeof(TrainStationModule), "<TransferQuantity>k__BackingField", batch);
            SetSettings(new TrainSettings(1, 1, 1, 1, 3, 4));
            SetField(station, typeof(TrainStationModule), "<IsForLoading>k__BackingField", true);
            Check(ReadTransfer(station) == batch, "loading scales cycle time instead of batch quantity");
            SetField(station, typeof(TrainStationModule), "<IsForLoading>k__BackingField", false);
            Check(ReadTransfer(station) == batch, "unloading scales cycle time instead of batch quantity");

            var notifications = new PropertyNotifications();
            var property = new PropertyPercentMult("test", Percent.Hundred, notifications);
            property.AddOrSetModifier("difficulty", TrainSettings.Factor(0.5), default);
            property.AddOrSetModifier("TrainParameterMod", new TrainSettings(1, 1, 2, 1, 1, 1).CapacityDelta, default);
            Check(property.Value == TrainSettings.Factor(3), "capacity stacks with difficulty multiplicatively");
            property.AddOrSetModifier("TrainParameterMod", TrainSettings.Vanilla.CapacityDelta, default);
            Check(property.Value == TrainSettings.Factor(1.5), "reset preserves difficulty bonus");
            Check(notifications.Count > 0, "native property change notifications emitted");
            CheckLiveSettings(root, train);
        }
        finally { harmony.UnpatchAll(TrainParameterMod.TrainParameterMod.Owner); SetSettings(TrainSettings.Vanilla); }
        Check(data.ScaleAccelerationBasedOnFunFactor(acceleration) == vanillaAcceleration, "patch teardown restores vanilla");
        LoadLifecycleChecks.Run(root, Check);
        MovementAndCargoChecks.Run(Check);
        CheckSteppedSettings(root);
    }

    private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    private static void CheckSteppedSettings(string root)
    {
        Check(ModJsonConfig.TryLoadFromFile(Path.Combine(root, "config.json"), "TrainParameterMod", out var draft, out _), "slider draft loads the native schema");
        foreach (var parameter in draft.Parameters.Values)
        {
            var setting = new SteppedSetting(draft, parameter);
            Check(setting.Value == 1, parameter.Name + " slider starts at 1.0");
            setting.SetTick(setting.Tick + 1);
            Check(setting.Value == 1.1 && draft.GetDouble(parameter.Name) == 1.1, parameter.Name + " moves by exactly 0.1");
            setting.SetTick(int.MaxValue);
            Check(setting.Value == parameter.Max, parameter.Name + " slider clamps to the upper bound");
            setting.SetTick(int.MinValue);
            Check(setting.Value == parameter.Min, parameter.Name + " slider clamps to the lower bound");
        }
        draft.TrySetValue("speed_multiplier", 0.25, out _);
        var oldSpeed = new SteppedSetting(draft, draft.Parameters["speed_multiplier"]);
        Check(oldSpeed.Value == 0.3 && draft.GetDouble("speed_multiplier") == 0.3,
            "an old non-tenth speed value is normalized only in the editable draft");
        draft.ResetToDefaults();
        oldSpeed.Refresh();
        Check(oldSpeed.Value == 1 && draft.GetDouble("speed_multiplier") == 1, "restore defaults updates the slider draft back to 1.0");
    }

    private static void CheckLiveSettings(string root, Train train)
    {
        Check(ModJsonConfig.TryLoadFromFile(Path.Combine(root, "config.json"), "TrainParameterMod", out var config, out _), "live config schema loads");
        var manifest = new ModManifest(root, "TrainParameterMod", default, "Train Parameter Mod",
            Mafi.Collections.ImmutableCollections.ImmutableArray<string>.Empty);
        var mod = new TrainParameterMod.TrainParameterMod(manifest);
        CheckMenuRegistration(mod);
        SetField(mod, typeof(DataOnlyMod), "<JsonConfig>k__BackingField", config);
        var notifications = new PropertyNotifications();
        var capacity = new PropertyPercentMult(Mafi.Core.IdsCore.PropertyIds.TrainsCapacityMultiplier.Value, Percent.Hundred, notifications);
        capacity.AddOrSetModifier("difficulty", TrainSettings.Factor(0.5), default);
        var properties = Empty<PropsDb>();
        var values = new Mafi.Collections.Dict<string, IProperty> { { capacity.Id, capacity } };
        SetField(properties, typeof(PropsDb), "m_properties", values);
        var trains = Empty<TrainsManager>();
        var trainList = new Mafi.Collections.Lyst<Train>();
        trainList.Add(train);
        SetField(trains, typeof(TrainsManager), "m_trains", trainList);
        SetField(mod, typeof(TrainParameterMod.TrainParameterMod), "properties", properties);
        SetField(mod, typeof(TrainParameterMod.TrainParameterMod), "capacityProperty", capacity);
        SetField(mod, typeof(TrainParameterMod.TrainParameterMod), "trains", trains);
        SetField(train, typeof(Train), "<IsReversed>k__BackingField", false);
        SetSettings(TrainSettings.Vanilla);
        TrainPatches.RefreshTopSpeed(train);

        mod.QueueSettings(new TrainSettings(0.5, 2, 2, 2, 3, 4));
        Check(config.GetDouble("speed_multiplier") == 1 && train.MaxSpeed == 2.Tiles(), "UI submission does not mutate game state before a sim callback");
        Check(mod.EditableSettings.Speed == 2, "reopening shows the latest submitted settings");
        var detachedDraft = mod.EditableSettings.ToValues();
        detachedDraft["speed_multiplier"] = 5.0;
        Check(mod.EditableSettings.Speed == 2, "editing a draft cannot change the queued snapshot");

        ProcessSettings(mod);
        Check(config.GetDouble("speed_multiplier") == 2 && train.MaxSpeed == 4.Tiles(), "simulation callback updates config and an existing train without reload");
        Check(capacity.Value == TrainSettings.Factor(3), "simulation callback updates live wagon-capacity property");
        Check(TrainParameterMod.TrainParameterMod.Settings.Loading == 3 && TrainParameterMod.TrainParameterMod.Settings.Unloading == 4,
            "one live apply publishes all settings together");
        var changeCount = notifications.Count;
        ProcessSettings(mod);
        Check(changeCount == notifications.Count, "idle sim callbacks do not reapply settings");

        ModJsonConfig.TryLoadFromFile(Path.Combine(root, "config.json"), "TrainParameterMod", out var restored, out _);
        restored.MergeSavedValues(config.GetSavedValues());
        var restoredSettings = TrainSettings.Read(restored);
        Check(restoredSettings.CurveSlowdown == 0.5 && restoredSettings.Acceleration == 2 && restoredSettings.Capacity == 2 &&
            restoredSettings.Speed == 2 && restoredSettings.Loading == 3 && restoredSettings.Unloading == 4,
            "live settings round-trip through the game's saved-value format");
        restored.TrySetValue("speed_multiplier", 5.0, out _);
        ProcessSettings(mod);
        Check(config.GetDouble("speed_multiplier") == 2 && train.MaxSpeed == 4.Tiles(), "canceling an edited draft leaves applied values unchanged");

        mod.QueueSettings(new TrainSettings(1, 1, 3, 3, 1, 1));
        mod.QueueSettings(TrainSettings.Vanilla);
        ProcessSettings(mod);
        Check(train.MaxSpeed == 2.Tiles() && capacity.Value == TrainSettings.Factor(1.5), "latest pending apply wins and defaults restore existing trains");
        Check(config.GetDouble("speed_multiplier") == 1, "restore defaults is persisted in mod config");
        mod.Dispose();
    }

    private static void ProcessSettings(TrainParameterMod.TrainParameterMod mod) =>
        typeof(TrainParameterMod.TrainParameterMod).GetMethod("ProcessSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mod, null);

    private static void CheckMenuRegistration(TrainParameterMod.TrainParameterMod mod)
    {
        var input = DispatchProxy.Create<Mafi.Unity.IUnityInputMgr, InputRecorder>();
        var recorder = (InputRecorder)(object)input;
        var context = new Mafi.Unity.InputControl.ControllerContext(input, null!, null!, null!);
        var menu = new LiveTrainSettingsMenu(mod, context);
        Check(ReferenceEquals(recorder.Registered, menu), "live menu registers with the native input manager");
        var bindings = recorder.Keys!(null!);
        Check(bindings.Primary.IsCode(UnityEngine.KeyCode.F8) && bindings.Mode == Mafi.Unity.InputControl.ShortcutMode.Game,
            "native shortcut opens live settings with F8 in game mode");
        Check(menu.Config.BlockShortcuts && menu.Config.DisableCameraControl,
            "typing into the live menu cannot trigger build or camera shortcuts");
        Check(menu.Config.Group == Mafi.Unity.InputControl.ControllerGroup.Window,
            "live menu uses a normal window controller rather than the pausing game-menu controller");
        menu.Dispose();
        Check(ReferenceEquals(recorder.Removed, menu), "unloading unregisters the native shortcut");
    }
    // Compile callers after patch installation, as the game does before starting its simulation.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RelTile1f ReadTrackSpeed(TrainTrackProto track) => track.MaxSpeedTilesPerTick;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PartialQuantity ReadTransfer(TrainStationModule station) => station.TransferQuantity;
    private static void SetField(object target, Type declaringType, string name, object value) =>
        declaringType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void SetSettings(TrainSettings value) =>
        typeof(TrainParameterMod.TrainParameterMod).GetField("settings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static int CountByOwner(this System.Collections.Generic.IEnumerable<MethodBase> methods, string owner)
    {
        var count = 0;
        foreach (var method in methods) if (Harmony.GetPatchInfo(method)?.Owners.Contains(owner) == true) count++;
        return count;
    }

    private sealed class PropertyNotifications : IPropertiesDbInternal
    {
        internal int Count;
        public void OnPropertyChanged(IProperty property, Mafi.Collections.ImmutableCollections.ImmutableArray<Type> types) => Count++;
    }

    public class InputRecorder : DispatchProxy
    {
        internal object? Registered;
        internal object? Removed;
        internal Func<Mafi.Unity.InputControl.ShortcutsManager, Mafi.Unity.InputControl.KeyBindings>? Keys;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "RegisterGlobalShortcut")
            {
                Keys = (Func<Mafi.Unity.InputControl.ShortcutsManager, Mafi.Unity.InputControl.KeyBindings>)args![0]!;
                Registered = args[1];
            }
            if (targetMethod.Name == "RemoveGlobalShortcut") Removed = args![0];
            if (targetMethod.ReturnType == typeof(void)) return null;
            throw new InvalidOperationException("Unexpected native input call: " + targetMethod.Name);
        }
    }
}
