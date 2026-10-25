using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Mods;
using Mafi.Core.PropertiesDb;
using Mafi.Core.Trains;
using Mafi.Core.Simulation;
using Mafi.Core.GameLoop;
using Mafi.Unity.InputControl;

namespace TrainParameterMod;

public sealed class TrainParameterMod : DataOnlyMod
{
    internal const string Owner = "TrainParameterMod";
    private Harmony? harmony;
    private PropsDb? properties;
    private TrainsManager? trains;
    private ISimLoopEvents? simEvents;
    private TrainSettings? pendingSettings;
    private LiveTrainSettingsMenu? liveMenu;
    private int settingsChanged;
    internal static TrainParameterMod? Active { get; private set; }
    private static TrainSettings settings = TrainSettings.Vanilla;
    internal static TrainSettings Settings => Volatile.Read(ref settings);
    internal TrainSettings EditableSettings => Volatile.Read(ref pendingSettings) ?? Settings;

    public TrainParameterMod(ModManifest manifest) : base(manifest) { }

    public override void RegisterPrototypes(ProtoRegistrator registrator) { }

    public override void EarlyInit(DependencyResolver resolver)
    {
        properties = resolver.Resolve<PropsDb>();
        trains = resolver.Resolve<TrainsManager>();
        simEvents = resolver.Resolve<ISimLoopEvents>();
        Volatile.Write(ref settings, TrainSettings.Read(JsonConfig));
        harmony = new Harmony(Owner);
        try
        {
            harmony.PatchAll(typeof(TrainParameterMod).Assembly);
            ApplySettings();
            simEvents.UpdateBeforeCmdProc.AddNonSaveable(this, ProcessSettings);
            JsonConfig.OnValueChanged += OnSettingChanged;
            Active = this;
            resolver.Resolve<IGameLoopEvents>().RegisterRendererInitState(this, InitializeMenu(resolver));
            Log.Info("Train Parameter Mod: active; live settings available with F8 or from the pause menu.");
        }
        catch
        {
            harmony.UnpatchAll(Owner);
            harmony = null;
            properties.GetProperty(IdsCore.PropertyIds.TrainsCapacityMultiplier).TryRemoveModifier(Owner);
            Volatile.Write(ref settings, TrainSettings.Vanilla);
            throw;
        }
    }

    private void OnSettingChanged(string parameter)
    {
        Interlocked.Exchange(ref settingsChanged, 1);
    }

    // The UI only submits immutable snapshots. Config/prototypes are mutated by ProcessSettings.
    internal void QueueSettings(TrainSettings next) => Interlocked.Exchange(ref pendingSettings, next);

    private IEnumerator<string> InitializeMenu(DependencyResolver resolver)
    {
        liveMenu = new LiveTrainSettingsMenu(this, resolver.Resolve<ControllerContext>());
        yield return "Train Parameter Mod live menu";
    }

    internal void OpenLiveMenu() => liveMenu?.ActivateSelf();

    private void ProcessSettings()
    {
        var next = Interlocked.Exchange(ref pendingSettings, null);
        var changed = Interlocked.Exchange(ref settingsChanged, 0) != 0;
        if (next == null && !changed) return;
        if (next != null) JsonConfig.MergeSavedValues(next.ToValues());
        ApplySettings();
        Log.Info("Train Parameter Mod: applied in-game settings on simulation thread.");
    }

    private void ApplySettings()
    {
        var next = TrainSettings.Read(JsonConfig);
        Volatile.Write(ref settings, next);
        properties!.GetProperty(IdsCore.PropertyIds.TrainsCapacityMultiplier)
            .AddOrSetModifier(Owner, next.CapacityDelta, Option<string>.Create(Owner));
        foreach (var train in trains!.Trains)
            TrainPatches.RefreshTopSpeed(train);
    }

    public override void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public override void Dispose()
    {
        JsonConfig.OnValueChanged -= OnSettingChanged;
        liveMenu?.Dispose();
        liveMenu = null;
        Interlocked.Exchange(ref pendingSettings, null);
        simEvents?.UpdateBeforeCmdProc.RemoveNonSaveable(this, ProcessSettings);
        harmony?.UnpatchAll(Owner);
        harmony = null;
        Volatile.Write(ref settings, TrainSettings.Vanilla);
        properties = null;
        trains = null;
        simEvents = null;
        Active = null;
        base.Dispose();
    }
}
