using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Mods;
using Mafi.Localization;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.Ui.GameMenu;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using UnityEngine;
using UnityEngine.UIElements;
using Label = Mafi.Unity.UiToolkit.Library.Label;
using Column = Mafi.Unity.UiToolkit.Library.Column;
using Button = Mafi.Unity.UiToolkit.Library.Button;

namespace TrainParameterMod;

internal sealed class TrainSettingsWindow : Window
{
    private readonly TrainParameterMod mod;
    private readonly ModJsonConfig draft;
    private readonly List<SliderRow> sliders = new List<SliderRow>();
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
        var scroll = new ScrollColumn().FlexGrow(1f).FlexShrink(1f).AlignSelfStretch().AlignItemsStretch();
        scroll.RootElement.style.minHeight = 0;
        foreach (var parameter in draft.Parameters.Values)
        {
            var row = new SliderRow(draft, parameter);
            sliders.Add(row);
            scroll.Add(row);
        }
        Body.Add(scroll);
        status = new Label("Apply changes without reloading. The simulation keeps running; your current pause state is preserved.".AsLoc());
        Body.Add(status);
        var buttons = new Row(4.pt());
        buttons.Add(new ButtonText("Restore defaults".AsLoc(), () => { draft.ResetToDefaults(); RefreshSliders(); }));
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
        RefreshSliders();
        status.Value("Apply changes without reloading. The simulation keeps running; your current pause state is preserved.".AsLoc());
    }

    private void RefreshSliders() { foreach (var slider in sliders) slider.Refresh(); }

    private sealed class SliderRow : Column
    {
        private readonly SteppedSetting setting;
        private readonly SliderWithIncrements slider;
        private readonly Label value;

        internal SliderRow(ModJsonConfig draft, ModJsonConfigParam parameter)
        {
            setting = new SteppedSetting(draft, parameter);
            this.Gap(2.pt()).Padding(4.pt()).AlignSelfStretch().AlignItemsStretch();
            var title = parameter.Name == "acceleration_multiplier" ? "Acceleration & braking multiplier"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parameter.Name.Replace('_', ' '));
            var heading = new Row(4.pt()).AlignSelfStretch().Width(100.Percent());
            heading.Add(new Label(title.AsLoc()).FlexGrow(1f));
            value = new Label().Width(60.px());
            heading.Add(value);
            Add(heading);
            slider = new SliderWithIncrements().Range(setting.Minimum, setting.Maximum)
                .OnValueChanged(Change).OnValueChangedForPreview(Change);
            slider.SetFocusable(true);
            slider.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.LeftArrow && evt.keyCode != KeyCode.RightArrow) return;
                Change(setting.Tick + (evt.keyCode == KeyCode.LeftArrow ? -1 : 1));
                evt.StopPropagation();
            });
            var input = new Row(4.pt()).AlignSelfStretch().Width(100.Percent());
            slider.FlexGrow(1f).FlexShrink(1f).AlignSelfStretch();
            slider.RootElement.style.minWidth = 0;
            input.Add(new ButtonText("−".AsLoc(), () => Change(setting.Tick - 1)).Width(32.px()).FlexShrink(0));
            input.Add(slider);
            input.Add(new ButtonText("+".AsLoc(), () => Change(setting.Tick + 1)).Width(32.px()).FlexShrink(0));
            Add(input, new Label(parameter.Description.AsLoc()));
            Refresh();
        }

        internal void Refresh() { setting.Refresh(); ShowValue(); }
        private void Change(int tick) { setting.SetTick(tick); ShowValue(); }
        private void ShowValue()
        {
            slider.Value(setting.Tick);
            value.Value((setting.Value.ToString("0.0", CultureInfo.CurrentCulture) + "×").AsLoc());
        }
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
