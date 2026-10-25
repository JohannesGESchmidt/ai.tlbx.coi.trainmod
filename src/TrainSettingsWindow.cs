using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Mods;
using Mafi.Localization;
using Mafi.Unity.InputControl.GameMenu.Settings;
using Mafi.Unity.Ui.GameMenu;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;

namespace TrainParameterMod;

internal sealed class TrainSettingsWindow : Window
{
    private readonly TrainParameterMod mod;
    private readonly ModJsonConfig draft;
    private readonly ModJsonConfigPanel panel;
    private readonly Label status;

    internal TrainSettingsWindow(TrainParameterMod mod) : base("Train Parameter Mod".AsLoc())
    {
        this.mod = mod;
        DarkMask();
        WindowSize(580.px(), 85.Percent());
        Body.Gap(4.pt());
        var path = Path.Combine(mod.Manifest.RootDirectoryPath, "config.json");
        if (!ModJsonConfig.TryLoadFromFile(path, mod.Manifest.Id, out draft, out var error))
            throw new InvalidOperationException("Could not load train settings schema: " + error);
        draft.MergeSavedValues(mod.EditableSettings.ToValues());
        panel = new ModJsonConfigPanel(draft);
        var scroll = new ScrollColumn().FlexGrow(1f).FlexShrink(1f);
        scroll.RootElement.style.minHeight = 0;
        scroll.Add(panel);
        Body.Add(scroll);
        status = new Label("Apply changes without reloading. The simulation keeps running; your current pause state is preserved.".AsLoc());
        Body.Add(status);
        var buttons = new Row(4.pt());
        buttons.Add(new ButtonText("Restore defaults".AsLoc(), () => { draft.ResetToDefaults(); panel.RefreshValues(); }));
        buttons.Add(new ButtonText(Button.Primary, "Apply".AsLoc(), () =>
        {
            mod.QueueSettings(TrainSettings.Read(draft));
            status.Value("Changes submitted for the next simulation update. No save or reload required.".AsLoc());
        }));
        buttons.Add(new ButtonText("Cancel / Close".AsLoc(), Close));
        Body.Add(buttons);
    }

    internal void RefreshDraft()
    {
        draft.MergeSavedValues(mod.EditableSettings.ToValues());
        panel.RefreshValues();
        status.Value("Apply changes without reloading. The simulation keeps running; your current pause state is preserved.".AsLoc());
    }
}

[HarmonyPatch]
internal static class PauseMenuPatch
{
    // The game menu has a dependency-injected constructor, not a parameterless constructor.
    private static MethodBase TargetMethod() => typeof(GameEscapeMenu)
        .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();

    private static void Postfix(GameEscapeMenu __instance)
    {
        var mod = TrainParameterMod.Active;
        if (mod == null) return;
        __instance.Body.Add(new ButtonText("Train Parameter Mod (F8)".AsLoc(), mod.OpenLiveMenu));
    }
}
