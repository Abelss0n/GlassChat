using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace GlassChat.Ui.ChatLog;

// A resize grip in the top-right corner of the main chat window instead of
// ImGui's bottom-right one. Dragging it moves the top and right edges and
// keeps the bottom-left corner in place, so a chat anchored to the bottom of
// the screen grows upward. ImGui has no option to move its own grip, so the
// built-in one is made invisible and this one mirrors its look and behaviour.
public partial class ChatLog
{
    private const string GripIdLabel = "##glasschat-resize-top-right";

    private bool GripActive;
    private int GripColourPushCount;
    private uint GripColour;
    private uint GripHoveredColour;
    private uint GripHeldColour;

    private bool GripHovered;
    private bool GripHeld;
    private bool GripWasHeld;
    private Vector2 GripStartMouse;
    private Vector2 GripStartPos;
    private Vector2 GripStartSize;

    /// <summary>
    /// Called from PreDraw, before the window begins: remembers the grip
    /// colours (theme included) and hides ImGui's own grip.
    /// </summary>
    private void HideBuiltInGrip()
    {
        GripActive = Plugin.Config.CanResize && !LogCollapsed;
        if (!GripActive)
            return;

        GripColour = ImGui.GetColorU32(ImGuiCol.ResizeGrip);
        GripHoveredColour = ImGui.GetColorU32(ImGuiCol.ResizeGripHovered);
        GripHeldColour = ImGui.GetColorU32(ImGuiCol.ResizeGripActive);

        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, 0u);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, 0u);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, 0u);
        GripColourPushCount = 3;
    }

    /// <summary>Called from PostDraw, after the window ends.</summary>
    private void RestoreBuiltInGrip()
    {
        if (GripColourPushCount > 0)
            ImGui.PopStyleColor(GripColourPushCount);

        GripColourPushCount = 0;
    }

    /// <summary>
    /// Handles the grip's input. Call at the start of the window's content so
    /// the grip takes the mouse before anything drawn under the corner.
    /// </summary>
    private unsafe void UpdateTopRightGrip()
    {
        GripHovered = false;
        GripHeld = false;

        if (GripActive && !ImGui.IsWindowDocked())
        {
            GetGripGeometry(out var windowMin, out var windowMax, out var corner, out var size, out _);

            // The grip sits outside the content clip rect, so widen it to the
            // whole window for the hit test, as ImGui does for its own grips.
            ImGui.PushClipRect(windowMin, windowMax, false);
            var bb = new ImRect(new Vector2(corner.X - size, corner.Y), new Vector2(corner.X, corner.Y + size));
            var id = ImGui.GetID(GripIdLabel);
            ImGuiP.ItemAdd(bb, id, ImGuiItemFlags.NoNav);

            bool hovered, held;
            const ImGuiButtonFlagsPrivate flags = ImGuiButtonFlagsPrivate.FlattenChildren | ImGuiButtonFlagsPrivate.NoNavFocus;
            ImGuiP.ButtonBehavior(bb, id, &hovered, &held, (ImGuiButtonFlags)flags);
            ImGui.PopClipRect();

            GripHovered = hovered;
            GripHeld = held;
        }

        if (GripHovered || GripHeld)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNesw);

        if (GripHeld && !GripWasHeld)
        {
            GripStartMouse = ImGui.GetIO().MousePos;
            GripStartPos = ImGui.GetWindowPos();
            GripStartSize = ImGui.GetWindowSize();
        }
        else if (!GripHeld && GripWasHeld)
        {
            // Back to the default so the saved window size is respected.
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        GripWasHeld = GripHeld;
    }

    /// <summary>
    /// Draws the grip and queues the new size for the next frame. Call at the
    /// end of the window's content, after <see cref="DrawChatLog"/> has
    /// cleared <see cref="Window.Position"/>.
    /// </summary>
    private void DrawTopRightGrip()
    {
        if (!GripActive || ImGui.IsWindowDocked())
            return;

        if (GripHeld)
        {
            var delta = ImGui.GetIO().MousePos - GripStartMouse;
            var minSize = ImGui.GetStyle().WindowMinSize;
            var width = MathF.Max(minSize.X, GripStartSize.X + delta.X);
            var height = MathF.Max(minSize.Y, GripStartSize.Y - delta.Y);

            // Applied by the window system before the next Begin. Size is in
            // unscaled units because the window system multiplies it by the
            // global scale.
            Position = new Vector2(GripStartPos.X, GripStartPos.Y + GripStartSize.Y - height);
            Size = new Vector2(width, height) / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
        }

        GetGripGeometry(out var windowMin, out var windowMax, out var corner, out var size, out var rounding);
        var border = ImGui.GetStyle().WindowBorderSize;
        var colour = GripHeld ? GripHeldColour : GripHovered ? GripHoveredColour : GripColour;

        // Same outline ImGui uses for its corner grips, mirrored to the top right.
        var drawList = ImGui.GetWindowDrawList();
        ImGui.PushClipRect(windowMin, windowMax, false);
        drawList.PathLineTo(corner + new Vector2(-border, size));
        drawList.PathLineTo(corner + new Vector2(-size, border));
        drawList.PathArcToFast(new Vector2(corner.X - (rounding + border), corner.Y + rounding + border), rounding, 9, 12);
        drawList.PathFillConvex(colour);
        ImGui.PopClipRect();
    }

    private void GetGripGeometry(out Vector2 windowMin, out Vector2 windowMax, out Vector2 corner, out float size, out float rounding)
    {
        windowMin = ImGui.GetWindowPos();
        windowMax = windowMin + ImGui.GetWindowSize();

        // With a title bar the grip sits just below it, where the corner is square.
        var titleBarHeight = Plugin.Config.ShowTitleBar ? ImGui.GetFrameHeight() : 0f;
        rounding = titleBarHeight > 0f ? 0f : ImGui.GetStyle().WindowRounding;
        corner = new Vector2(windowMax.X, windowMin.Y + titleBarHeight);

        var fontSize = ImGui.GetFontSize();
        size = MathF.Floor(MathF.Max(fontSize * 1.35f, rounding + 1f + fontSize * 0.2f));
    }
}
