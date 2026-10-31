using System;
using Mafi.Unity.InputControl;
using UnityEngine;

namespace TrainParameterMod;

/// <summary>Uses the game's input/window lifecycle; opening this controller does not request a pause.</summary>
internal sealed class LiveTrainSettingsMenu : WindowController<TrainSettingsWindow>, IDisposable
{
    internal static readonly KbHandle OpenSettingsShortcut = KbHandleBuilder
        .Start("TrainParameterMod.OpenSettings", "Train Parameter Mod")
        .SetGroup(KbGroups.Windows)
        .SetMode(ShortcutMode.Game)
        .SetPrimary(KeyCode.F8)
        .Build();

    private readonly TrainParameterMod mod;

    internal LiveTrainSettingsMenu(TrainParameterMod mod, ControllerContext context) : base(context, MenuConfig())
    {
        this.mod = mod;
        context.InputManager.RegisterGlobalShortcut(OpenSettingsShortcut, this);
    }

    private static ControllerConfig MenuConfig()
    {
        var config = ControllerConfig.Window;
        config.BlockShortcuts = true;
        config.DisableCameraControl = true;
        return config;
    }

    protected override TrainSettingsWindow CreateWindow() => new TrainSettingsWindow(mod);

    protected override void OnActivate() => Window.RefreshDraft();

    public void Dispose()
    {
        Context.InputManager.RemoveGlobalShortcut(this);
        if (IsActive) DeactivateSelf();
    }
}
