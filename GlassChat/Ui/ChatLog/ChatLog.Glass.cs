using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace GlassChat.Ui.ChatLog;

// Glass touches inside the main chat window: a thin glass strip behind the
// input bar and a breathing glow around tabs with unread messages. Both are
// drawn underneath their items, so they use the item's rect from the previous
// frame; the one-frame lag is invisible for things that rarely move.
public partial class ChatLog
{
    private Vector2[] TabRectMin = [];
    private Vector2[] TabRectMax = [];

    private float BarTop;
    private float BarHeight;

    /// <summary>Glows a tab with unread messages. Call right before the tab is submitted.</summary>
    private void DrawTabAttention(int index, Tab tab)
    {
        if (!Plugin.Glass.Enabled || index == Plugin.LastTab || tab.UnreadMode == UnreadMode.None || tab.Unread == 0)
            return;

        EnsureTabRects();
        var min = TabRectMin[index];
        var max = TabRectMax[index];
        if (max.X <= min.X)
            return;

        Plugin.Glass.DrawAttentionGlow(ImGui.GetWindowDrawList(), min, max, ImGui.GetStyle().TabRounding);
    }

    /// <summary>Remembers a tab's rect for next frame's glow. Call right after the tab is submitted.</summary>
    private void RememberTabRect(int index)
    {
        EnsureTabRects();
        TabRectMin[index] = ImGui.GetItemRectMin();
        TabRectMax[index] = ImGui.GetItemRectMax();
    }

    private void EnsureTabRects()
    {
        var count = Plugin.Config.Tabs.Count;
        if (TabRectMin.Length == count)
            return;

        // Only when tabs are added or removed.
        TabRectMin = new Vector2[count];
        TabRectMax = new Vector2[count];
    }

    /// <summary>
    /// Draws the glass strip behind the input bar (channel name and input
    /// row). Call right before the channel name line, then
    /// <see cref="EndBarGlass"/> after the input row.
    /// </summary>
    private void BeginBarGlass()
    {
        var pos = ImGui.GetCursorScreenPos();
        BarTop = pos.Y;
        if (BarHeight <= 0f || !Plugin.Glass.Enabled)
            return;

        var pad = 4f * ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;

        // Corners concentric with the input box inside the strip: its own
        // rounding plus the padding between them.
        var radius = ImGui.GetStyle().FrameRounding + pad;
        Plugin.Glass.DrawBarGlass(ImGui.GetWindowDrawList(), new Vector2(pos.X - pad, BarTop - pad), new Vector2(pos.X + width + pad, BarTop + BarHeight + pad), radius);
    }

    /// <summary>Measures the bar for next frame. Call right after the input row's last item.</summary>
    private void EndBarGlass()
    {
        BarHeight = ImGui.GetItemRectMax().Y - BarTop;
    }
}
