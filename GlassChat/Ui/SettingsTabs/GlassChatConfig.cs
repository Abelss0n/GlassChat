using GlassChat.Resources;
using GlassChat.Util;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace GlassChat.Ui.SettingsTabs;

/// <summary>Settings for the features GlassChat adds on top of Chat 2.</summary>
public sealed class GlassChatConfig : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name { get; } = $"{Plugin.PluginName}###tabs-glasschat";

    public GlassChatConfig(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImRaii.TextWrapPos(0.0f);

        DrawHeading(Language.Options_GlassChat_GlassHeader);

        ImGuiUtil.OptionCheckbox(ref Mutable.GlassEnabled, Language.Options_Glass_Name, Language.Options_Glass_Description);
        ImGui.Spacing();

        if (Mutable.GlassEnabled)
        {
            using var _ = ImRaii.PushIndent();
            ImGuiUtil.OptionCheckbox(ref Mutable.GlassReducedQuality, Language.Options_GlassReducedQuality_Name, Language.Options_GlassReducedQuality_Description);
            ImGui.Spacing();

            ImGuiUtil.OptionCheckbox(ref Mutable.GlassCustomFrost, Language.Options_GlassFrost_Name, Language.Options_GlassFrost_Description);
            if (Mutable.GlassCustomFrost)
            {
                ImGui.SetNextItemWidth(-1);
                var percent = Mutable.GlassFrost * 100f;
                if (ImGui.SliderFloat("##glass-frost", ref percent, 0f, 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
                    Mutable.GlassFrost = percent / 100f;
            }
            ImGui.Spacing();

            DrawThemePicker();
            ImGui.Spacing();

            ImGuiUtil.OptionCheckbox(ref Mutable.GlassInputBar, Language.Options_GlassInputBar_Name, Language.Options_GlassInputBar_Description);
            ImGui.Spacing();

            ImGuiUtil.OptionCheckbox(ref Mutable.GlassReducedMotion, Language.Options_GlassReducedMotion_Name, Language.Options_GlassReducedMotion_Description);
            ImGui.Spacing();
        }

        ImGui.Spacing();
        DrawHeading(Language.Options_GlassChat_ChatBarHeader);

        ImGuiUtil.OptionCheckbox(ref Mutable.ChatLogAutoCollapse, Language.Options_AutoCollapse_Name, Language.Options_AutoCollapse_Description);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.ChatLogCollapsedPreview, Language.Options_CollapsedPreview_Name, Language.Options_CollapsedPreview_Description);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.ChatLogIdleMinimise, Language.Options_IdleMinimise_Name, Language.Options_IdleMinimise_Description);
        if (Mutable.ChatLogIdleMinimise)
        {
            using var _ = ImRaii.PushIndent();
            if (ImGuiUtil.InputIntVertical(Language.Options_IdleMinimiseSeconds_Name, Language.Options_IdleMinimiseSeconds_Description, ref Mutable.ChatLogIdleMinimiseSeconds, 1, 10))
                Mutable.ChatLogIdleMinimiseSeconds = Math.Clamp(Mutable.ChatLogIdleMinimiseSeconds, 3, 600);
        }
        ImGui.Spacing();
    }

    private static void DrawHeading(string text)
    {
        ImGui.TextUnformatted(text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawThemePicker()
    {
        var preview = GlassHost.ThemeName(Mutable.GlassTheme) ?? Language.Options_GlassTheme_Default;
        using (var combo = ImGuiUtil.BeginComboVertical(Language.Options_GlassTheme_Name, preview))
        {
            if (combo)
            {
                if (ImGui.Selectable(Language.Options_GlassTheme_Default, string.IsNullOrEmpty(Mutable.GlassTheme)))
                    Mutable.GlassTheme = null;

                var keys = GlassHost.ThemeKeys;
                var names = GlassHost.ThemeNames;
                for (var i = 0; i < keys.Length; i++)
                    if (ImGui.Selectable($"{names[i]}##theme-{keys[i]}", Mutable.GlassTheme == keys[i]))
                        Mutable.GlassTheme = keys[i];
            }
        }

        ImGuiUtil.HelpText(Language.Options_GlassTheme_Description);
    }
}
