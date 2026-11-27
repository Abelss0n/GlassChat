using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using GlassChat.Resources;
using GlassChat.Util;

namespace GlassChat.Ui.ChatLog;

// Collapses the main chat window down to its input bar, hiding the tabs and
// messages to reduce clutter. The bottom edge stays put, so the bar stays
// where it was on screen. Expanding restores the height the window had
// before. Starting to chat (Enter, slash, reply) while collapsed expands it,
// and with ChatLogAutoCollapse on, sending or pressing Escape collapses it
// again. Expanding with the button keeps it open.
//
// While collapsed, new messages in the current tab show briefly above the bar
// and fade out, and the expand button glows once someone writes (a player
// message, not system spam). Optionally the window collapses itself after a
// stretch without use.
public partial class ChatLog
{
    private static readonly string CollapseLabel = $"{FontAwesomeIcon.CaretDown.ToIconString()}##glasschat-collapse";
    private static readonly string ExpandLabel = $"{FontAwesomeIcon.CaretUp.ToIconString()}##glasschat-collapse";

    /// <summary>The height to fall back to when none was recorded, in unscaled units.</summary>
    private const float DefaultExpandedHeight = 250f;

    /// <summary>How long a new message shows above the collapsed bar, fade included.</summary>
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(8);

    /// <summary>The last part of <see cref="PreviewDuration"/> spent fading out, in seconds.</summary>
    private const double PreviewFadeSeconds = 1.5;

    /// <summary>The most messages shown above the collapsed bar at once.</summary>
    private const int MaxPreviewMessages = 3;

    /// <summary>
    /// Messages arriving this soon after collapsing don't make the expand
    /// button glow: they are usually the echo of the message just sent.
    /// </summary>
    private static readonly TimeSpan NoticeGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>True while a forced size from collapsing or expanding waits to be released.</summary>
    private bool CollapseSizeForced;

    /// <summary>
    /// True while the window is open only because chat was started while it
    /// was collapsed, so it collapses again when the message is sent or
    /// abandoned. Not saved: after a reload the window simply stays as it is.
    /// </summary>
    private bool ExpandedForChat;

    // Collapsed preview and notice.
    private readonly Message[] PreviewMessages = new Message[MaxPreviewMessages];
    private int PreviewCount;
    private DateTimeOffset NoticeAfter = DateTimeOffset.MaxValue;
    private bool HasNotice;
    private Vector2 CollapseButtonMin;
    private Vector2 CollapseButtonMax;

    // Idle minimise: the last frame the window was in use, in ms ticks.
    private long LastInteraction;

    // Bottom anchoring while collapsed. A height change only applies on the
    // next frame, so laying the content out top-down would push the bar down
    // and back up whenever the preview grows or shrinks (or right after
    // collapsing). Instead the footer (input preview, channel name and input
    // row) is kept at a fixed distance from the window's bottom edge, and the
    // preview sits directly above it; only the top edge ever moves.
    private float FooterTopScreenY;
    private float FooterHeight;
    private float CollapsedStartY;
    private float CollapsedPreviewHeight;

    /// <summary>
    /// True when this frame drew the collapsed layout. Collapsing with the
    /// button happens partway through drawing the expanded one, and that
    /// frame has no collapsed measurements to size the window with.
    /// </summary>
    private bool DrewCollapsedLayout;

    public bool LogCollapsed => Plugin.Config.ChatLogCollapsed;

    /// <summary>
    /// Called when chat starts (Enter, slash, reply). Opens a collapsed
    /// window for the duration of the message.
    /// </summary>
    private void ExpandForChat()
    {
        if (!LogCollapsed)
            return;

        SetCollapsed(false);
        ExpandedForChat = true;
    }

    /// <summary>
    /// Called by the input handler when a message was sent or abandoned with
    /// Escape. Collapses the window again if chatting is what opened it.
    /// </summary>
    public void ChatInputFinished()
    {
        if (!ExpandedForChat)
            return;

        ExpandedForChat = false;
        if (Plugin.Config.ChatLogAutoCollapse)
            SetCollapsed(true);
    }

    /// <summary>
    /// Collapses or expands the message area. Safe to call outside the
    /// window's draw: it only uses the window rect from the last frame and
    /// queues the new rect for the next one.
    /// </summary>
    public void SetCollapsed(bool collapsed)
    {
        if (collapsed == Plugin.Config.ChatLogCollapsed)
            return;

        HasNotice = false;
        if (collapsed)
        {
            Plugin.Config.ChatLogExpandedHeight = LastWindowSize.Y;
            NoticeAfter = DateTimeOffset.UtcNow + NoticeGrace;
        }
        else
        {
            var height = Plugin.Config.ChatLogExpandedHeight;
            if (height <= 0f)
                height = DefaultExpandedHeight * ImGuiHelpers.GlobalScale;

            ResizeKeepingBottom(height);
            NoticeAfter = DateTimeOffset.MaxValue;
            LastInteraction = Environment.TickCount64;
        }

        Plugin.Config.ChatLogCollapsed = collapsed;
        Plugin.SaveConfig();
    }

    /// <summary>
    /// Releases a forced size once it has been applied. Call at the start of
    /// the window's content, when this frame's Begin has already used it.
    /// </summary>
    private void ReleaseCollapseSize()
    {
        if (!CollapseSizeForced || GripHeld)
            return;

        SizeCondition = ImGuiCond.FirstUseEver;
        CollapseSizeForced = false;
    }

    /// <summary>
    /// The triangle button, right-aligned on the channel name line, in the
    /// same column as the buttons at the end of the input row.
    /// </summary>
    private void DrawCollapseButton()
    {
        var width = ImGuiUtil.CalcIconButtonSize().X;

        ImGui.SameLine();
        var free = ImGui.GetContentRegionAvail().X - width;
        if (free > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + free);

        // Someone wrote while collapsed. The glow uses last frame's button
        // rect so it can be drawn underneath the button.
        var notice = LogCollapsed && HasNotice && Plugin.Config.ChatLogCollapsedPreview;
        if (notice && Plugin.Glass.Enabled)
            Plugin.Glass.DrawAttentionGlow(ImGui.GetWindowDrawList(), CollapseButtonMin, CollapseButtonMax, ImGui.GetStyle().FrameRounding);

        // No vertical padding, so the button is no taller than the text line
        // and the input row below doesn't move.
        var padding = ImGui.GetStyle().FramePadding;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, padding with { Y = 0f });
        var highlight = notice && !Plugin.Glass.Enabled;
        if (highlight)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));

        bool clicked;
        using (Plugin.FontManager.FontAwesome.Push())
            clicked = ImGui.Button(LogCollapsed ? ExpandLabel : CollapseLabel, new Vector2(width, 0f));

        if (highlight)
            ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        CollapseButtonMin = ImGui.GetItemRectMin();
        CollapseButtonMax = ImGui.GetItemRectMax();

        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(LogCollapsed ? Language.ChatLog_Expand_Tooltip : Language.ChatLog_Collapse_Tooltip);

        if (clicked)
        {
            // A deliberate choice: stop collapsing automatically after chat.
            ExpandedForChat = false;
            SetCollapsed(!LogCollapsed);
        }
    }

    /// <summary>
    /// While collapsed, finds new messages in the current tab and draws the
    /// recent ones above the bar. Call where the tabs would be drawn.
    /// </summary>
    private void DrawCollapsedPreview(Tab tab)
    {
        var now = DateTimeOffset.UtcNow;
        ScanCollapsedMessages(tab, now);

        var drawList = ImGui.GetWindowDrawList();
        var firstVertex = drawList.VtxBuffer.Size;
        CollapsedStartY = ImGui.GetCursorPosY();
        DrewCollapsedLayout = true;

        if (Plugin.Config.ChatLogCollapsedPreview && PreviewCount > 0)
            DrawPreviewMessages(now);

        CollapsedPreviewHeight = ImGui.GetCursorPosY() - CollapsedStartY;

        // Anchor the footer to the bottom edge, using its height from the
        // last frame (it practically never changes), and move the preview,
        // already drawn from the top, to sit right above it. ImGui can't lay
        // out upward, so the preview's vertices are shifted instead.
        if (FooterHeight <= 0f)
            return;

        var footerTop = FooterTopY();
        var shift = footerTop - CollapsedPreviewHeight - CollapsedStartY;
        if (CollapsedPreviewHeight > 0f && shift != 0f)
            ShiftVertices(drawList, firstVertex, shift);

        ImGui.SetCursorPosY(footerTop);
    }

    /// <summary>
    /// Where the footer starts so that it ends exactly at the window's bottom
    /// padding, in window coordinates. The same in both modes, so the bar
    /// doesn't move when the window collapses or expands.
    /// </summary>
    private float FooterTopY()
        => ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - FooterHeight;

    /// <summary>
    /// Expanded counterpart of the collapsed anchoring: puts the footer at
    /// <see cref="FooterTopY"/> after the tabs and message log. Call right
    /// after they are drawn.
    /// </summary>
    private void AnchorFooter()
    {
        if (FooterHeight > 0f)
            ImGui.SetCursorPosY(FooterTopY());
    }

    private static unsafe void ShiftVertices(ImDrawListPtr drawList, int firstVertex, float dy)
    {
        var buffer = drawList.VtxBuffer;
        var vertices = buffer.Data;
        for (var i = firstVertex; i < buffer.Size; i++)
            vertices[i].Pos.Y += dy;
    }

    private void DrawPreviewMessages(DateTimeOffset now)
    {
        var lineWidth = ImGui.GetContentRegionAvail().X;
        var baseAlpha = ImGui.GetStyle().Alpha;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        // Collected newest first; draw oldest first so the newest sits right
        // above the bar.
        for (var i = PreviewCount - 1; i >= 0; i--)
        {
            var message = PreviewMessages[i];
            var remaining = (PreviewDuration - (now - message.Date)).TotalSeconds;
            var alpha = (float)Math.Clamp(remaining / PreviewFadeSeconds, 0.0, 1.0);

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, baseAlpha * alpha);
            if (message.Sender.Count > 0)
            {
                InputHandler.ChunkHandler.DrawChunks(message.Sender, true, null, lineWidth);
                ImGui.SameLine();
            }

            InputHandler.ChunkHandler.DrawChunks(message.Content, true, null, lineWidth);
            ImGui.PopStyleVar();
        }

        ImGui.PopStyleVar();
        ImGui.Spacing();
        Array.Clear(PreviewMessages);
    }

    /// <summary>
    /// Walks the current tab's messages from the newest back, only as far as
    /// either feature needs: messages new since the last scan (for the
    /// notice) and messages young enough to preview.
    /// </summary>
    private void ScanCollapsedMessages(Tab tab, DateTimeOffset now)
    {
        PreviewCount = 0;
        var wantPreview = Plugin.Config.ChatLogCollapsedPreview;

        // Loaded already collapsed (state is saved): start counting from now.
        if (NoticeAfter == DateTimeOffset.MaxValue)
            NoticeAfter = now;

        try
        {
            // This may produce ApplicationException, like the message log.
            using var messages = tab.Messages.GetReadOnly(3);
            var newest = NoticeAfter;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];
                var isNew = message.Date > NoticeAfter;
                var isRecent = wantPreview && PreviewCount < MaxPreviewMessages && now - message.Date < PreviewDuration;
                if (!isNew && !isRecent)
                    break;

                if (isNew)
                {
                    if (message.Code.IsPlayerMessage())
                        HasNotice = true;

                    if (message.Date > newest)
                        newest = message.Date;
                }

                if (isRecent)
                    PreviewMessages[PreviewCount++] = message;
            }

            // Only messages after this one count next frame.
            NoticeAfter = newest;
        }
        catch (ApplicationException)
        {
            // Couldn't get the lock quickly; try again next frame.
        }
    }

    /// <summary>
    /// Marks where the footer (input preview, channel name and input row)
    /// starts. Call right before it, in both modes, so its height is known
    /// even on the first frame after collapsing.
    /// </summary>
    private void MarkFooterTop()
    {
        FooterTopScreenY = ImGui.GetCursorScreenPos().Y;
    }

    /// <summary>
    /// Measures the footer, and while collapsed sizes the window to fit the
    /// preview and footer. Call right after the input row's last item.
    /// </summary>
    private void FitCollapsedHeight()
    {
        FooterHeight = ImGui.GetItemRectMax().Y - FooterTopScreenY;

        var drewCollapsed = DrewCollapsedLayout;
        DrewCollapsedLayout = false;
        if (!LogCollapsed || !drewCollapsed || ImGui.IsWindowDocked())
            return;

        var needed = CollapsedStartY + CollapsedPreviewHeight + FooterHeight + ImGui.GetStyle().WindowPadding.Y;
        if (MathF.Abs(needed - LastWindowSize.Y) > 0.5f)
            ResizeKeepingBottom(needed);
    }

    /// <summary>
    /// Collapses the window after it has gone unused for the configured time:
    /// no mouse over it, no typing, no popup of its own open. Call once per
    /// frame from the window's content.
    /// </summary>
    private void UpdateIdleMinimise()
    {
        var now = Environment.TickCount64;
        var inUse = LogCollapsed
                    || !Plugin.Config.ChatLogIdleMinimise
                    || LastInteraction == 0
                    || InputHandler.InputFocused
                    || InputHandler.Activate
                    || GripHeld
                    || ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                    || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);

        if (inUse)
        {
            LastInteraction = now;
            return;
        }

        if (now - LastInteraction < Plugin.Config.ChatLogIdleMinimiseSeconds * 1000L)
            return;

        ExpandedForChat = false;
        SetCollapsed(true);
    }

    /// <summary>Queues a new height for the next frame, keeping the bottom edge in place.</summary>
    private void ResizeKeepingBottom(float height)
    {
        var bottom = LastWindowPos.Y + LastWindowSize.Y;

        // Applied by the window system before the next Begin. Size is in
        // unscaled units because the window system multiplies it by the
        // global scale.
        Position = new Vector2(LastWindowPos.X, bottom - height);
        Size = new Vector2(LastWindowSize.X, height) / ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.Always;
        CollapseSizeForced = true;
    }
}
