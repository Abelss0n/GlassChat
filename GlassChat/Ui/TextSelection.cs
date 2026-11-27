using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace GlassChat.Ui;

/// <summary>
/// Click-and-drag text selection in the message logs, copied with Ctrl+C.
/// </summary>
/// <remarks>
/// Messages aren't ImGui text widgets: each is drawn as wrapped pieces of its
/// chunks. While a log is drawn, every piece reports its message, its byte
/// offset in that message's text (see <see cref="MessageText"/>) and its rect,
/// which is enough to hit-test the mouse, highlight the selection and rebuild
/// the selected text. Positions are (message index in the tab, UTF-8 byte
/// offset in the message's text). One selection exists at a time, in one tab.
/// Everything runs on the draw thread and nothing allocates per frame.
/// </remarks>
public static class TextSelection
{
    private const string ActiveIdLabel = "##glasschat-text-selection";

    // The selection.
    private static Guid SelectionTab;
    private static bool HasSelection;
    private static bool Dragging;
    private static int AnchorIndex;
    private static int AnchorOffset;
    private static int FocusIndex;
    private static int FocusOffset;
    private static Message? AnchorMessage;
    private static Message? FocusMessage;
    private static uint ActiveId;

    // The log being drawn this frame.
    private static bool Recording;
    private static Guid RecordingTab;
    private static uint HighlightColour;
    private static Vector2 Mouse;
    private static Message? CurrentMessage;
    private static int CurrentIndex = -1;
    private static int Cursor; // byte offset of the next chunk in the current message

    // The text position nearest the mouse this frame.
    private static bool HitFound;
    private static float HitScore;
    private static int HitIndex;
    private static int HitOffset;
    private static bool HitOnLink;
    private static Message? HitMessage;

    /// <summary>Starts recording a log. Call inside the log's child window, before its messages.</summary>
    public static void BeginLog(Tab tab)
    {
        Recording = true;
        RecordingTab = tab.Identifier;
        HighlightColour = ImGui.GetColorU32(ImGuiCol.TextSelectedBg);
        Mouse = ImGui.GetMousePos();
        HitFound = false;
        HitOnLink = false;
        CurrentMessage = null;
        CurrentIndex = -1;
    }

    /// <summary>Starts a message. Call before its sender is drawn.</summary>
    public static void BeginMessage(Message message, int index)
    {
        if (!Recording)
            return;

        CurrentMessage = message;
        CurrentIndex = index;
        Cursor = 0;

        // Messages before the selection get pruned, which shifts indices;
        // refresh them whenever the selected messages are drawn.
        if (ReferenceEquals(message, AnchorMessage))
            AnchorIndex = index;
        if (ReferenceEquals(message, FocusMessage))
            FocusIndex = index;
    }

    /// <summary>Ends a message. Call after its content is drawn.</summary>
    public static void EndMessage()
    {
        CurrentMessage = null;
        CurrentIndex = -1;
    }

    /// <summary>The byte offset the next chunk of the current message starts at, or -1 outside a message.</summary>
    public static int ChunkStart => CurrentIndex < 0 ? -1 : Cursor;

    /// <summary>Moves past a chunk of the given UTF-8 length.</summary>
    public static void EndChunk(int length)
    {
        if (CurrentIndex >= 0)
            Cursor += length;
    }

    /// <summary>
    /// Reports a piece of text just drawn with the current font. Its bytes
    /// are [text, textEnd), at <paramref name="offset"/> in the message's text.
    /// </summary>
    public static unsafe void Text(byte* text, byte* textEnd, int offset, Vector2 min, Vector2 size, bool isLink)
    {
        if (CurrentIndex < 0)
            return;

        var length = (int)(textEnd - text);
        var max = min + size;

        // Hit test: prefer the same row, then the nearest along it.
        var dy = Mouse.Y < min.Y ? min.Y - Mouse.Y : Mouse.Y > max.Y ? Mouse.Y - max.Y : 0f;
        var dx = Mouse.X < min.X ? min.X - Mouse.X : Mouse.X > max.X ? Mouse.X - max.X : 0f;
        var score = dy * 10_000f + dx;
        if (!HitFound || score < HitScore)
        {
            HitFound = true;
            HitScore = score;
            HitIndex = CurrentIndex;
            HitMessage = CurrentMessage;
            HitOnLink = isLink && score == 0f;

            int local;
            if (dy > 0f)
                local = Mouse.Y < min.Y ? 0 : length;
            else if (Mouse.X <= min.X)
                local = 0;
            else if (Mouse.X >= max.X)
                local = length;
            else
                local = ByteAt(text, textEnd, Mouse.X - min.X);

            HitOffset = offset + local;
        }

        // Highlight the selected part of this piece.
        if (!HasSelection || RecordingTab != SelectionTab)
            return;

        Ordered(out var startIndex, out var startOffset, out var endIndex, out var endOffset);
        if (CurrentIndex < startIndex || CurrentIndex > endIndex)
            return;

        var from = CurrentIndex == startIndex ? Math.Max(startOffset - offset, 0) : 0;
        var to = CurrentIndex == endIndex ? Math.Min(endOffset - offset, length) : length;
        if (from >= to)
            return;

        var x0 = min.X + Width(text, text + from);
        var x1 = min.X + Width(text, text + to);
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(x0, min.Y), new Vector2(x1, max.Y), HighlightColour);
    }

    /// <summary>
    /// Reports something drawn as a whole (an emote image) standing for
    /// <paramref name="length"/> bytes of the message's text.
    /// </summary>
    public static unsafe void Atomic(int length, Vector2 min, Vector2 size)
    {
        if (CurrentIndex < 0)
            return;

        // Treat it as one wide character: no bytes to measure inside it.
        var offset = Cursor;
        var max = min + size;
        var dy = Mouse.Y < min.Y ? min.Y - Mouse.Y : Mouse.Y > max.Y ? Mouse.Y - max.Y : 0f;
        var dx = Mouse.X < min.X ? min.X - Mouse.X : Mouse.X > max.X ? Mouse.X - max.X : 0f;
        var score = dy * 10_000f + dx;
        if (!HitFound || score < HitScore)
        {
            HitFound = true;
            HitScore = score;
            HitIndex = CurrentIndex;
            HitMessage = CurrentMessage;
            HitOnLink = false;
            var before = dy > 0f ? Mouse.Y < min.Y : Mouse.X < (min.X + max.X) * 0.5f;
            HitOffset = offset + (before ? 0 : length);
        }

        if (HasSelection && RecordingTab == SelectionTab)
        {
            Ordered(out var si, out var so, out var ei, out var eo);
            var start = CurrentIndex == si ? so : 0;
            var end = CurrentIndex == ei ? eo : int.MaxValue;
            if (CurrentIndex >= si && CurrentIndex <= ei && start < offset + length && end > offset)
                ImGui.GetWindowDrawList().AddRectFilled(min, max, HighlightColour);
        }

        Cursor += length;
    }

    /// <summary>
    /// Ends recording and handles the mouse: a press on text starts a
    /// selection, dragging extends it (scrolling the log at its edges), and a
    /// click without a drag clears it. Call inside the log's child window,
    /// after its messages.
    /// </summary>
    public static void EndLog()
    {
        if (!Recording)
            return;

        Recording = false;
        CurrentMessage = null;
        CurrentIndex = -1;

        var logHovered = ImGui.IsWindowHovered();
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && logHovered)
        {
            if (!HitFound || HitOnLink)
            {
                // A click on a link keeps doing what it did; elsewhere clears.
                Clear();
                return;
            }

            SelectionTab = RecordingTab;
            AnchorIndex = FocusIndex = HitIndex;
            AnchorOffset = FocusOffset = HitOffset;
            AnchorMessage = FocusMessage = HitMessage;
            HasSelection = false;
            Dragging = true;

            // Owning the mouse stops ImGui from treating the drag as moving
            // the window (text counts as empty space to it). That also skips
            // ImGui's click-to-focus, so focus the log here: Ctrl+C only
            // copies from a focused window.
            ActiveId = ImGui.GetID(ActiveIdLabel);
            ImGuiP.SetActiveID(ActiveId, ImGuiP.GetCurrentWindow());
            ImGui.SetWindowFocus();
            return;
        }

        if (!Dragging || RecordingTab != SelectionTab)
            return;

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Dragging = false;
            if (ImGuiP.GetActiveID() == ActiveId)
                ImGuiP.ClearActiveID();
            return;
        }

        ImGuiP.KeepAliveID(ActiveId);
        if (HitFound)
        {
            FocusIndex = HitIndex;
            FocusOffset = HitOffset;
            FocusMessage = HitMessage;
            HasSelection = FocusIndex != AnchorIndex || FocusOffset != AnchorOffset;
        }

        // Scroll while the mouse is above or below the log.
        var top = ImGui.GetWindowPos().Y;
        var bottom = top + ImGui.GetWindowHeight();
        if (Mouse.Y < top)
            ImGui.SetScrollY(MathF.Max(0f, ImGui.GetScrollY() - (top - Mouse.Y) * 0.5f));
        else if (Mouse.Y > bottom)
            ImGui.SetScrollY(MathF.Min(ImGui.GetScrollMaxY(), ImGui.GetScrollY() + (Mouse.Y - bottom) * 0.5f));
    }

    /// <summary>
    /// Copies the selection to the clipboard on Ctrl+C. Call once per frame
    /// from a window showing <paramref name="tab"/>'s log.
    /// </summary>
    public static void HandleCopy(Tab tab)
    {
        if (!HasSelection || tab.Identifier != SelectionTab)
            return;

        var io = ImGui.GetIO();
        if (!io.KeyCtrl || !ImGui.IsKeyPressed(ImGuiKey.C, false) || io.WantTextInput)
            return;

        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;

        var text = SelectedText(tab);
        if (text.Length > 0)
            ImGui.SetClipboardText(text);
    }

    /// <summary>Drops the selection.</summary>
    public static void Clear()
    {
        HasSelection = false;
        Dragging = false;
        AnchorMessage = FocusMessage = null;
        if (ActiveId != 0 && ImGuiP.GetActiveID() == ActiveId)
            ImGuiP.ClearActiveID();
    }

    /// <summary>
    /// A message's selectable text: its sender and content chunks as drawn,
    /// line breaks as "\n". Offsets in <see cref="Text"/> index its UTF-8 bytes.
    /// </summary>
    public static string MessageText(Message message)
    {
        var builder = new StringBuilder();
        Append(builder, message.Sender);
        Append(builder, message.Content);
        return builder.ToString();
    }

    /// <summary>Normalises line breaks the way the text wrapper splits them.</summary>
    public static string NormaliseLineBreaks(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static void Append(StringBuilder builder, List<Chunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            var text = Handler.ChunkHandler.DisplayText(chunk);
            if (!string.IsNullOrEmpty(text))
                builder.Append(NormaliseLineBreaks(text));
        }
    }

    private static string SelectedText(Tab tab)
    {
        Ordered(out var startIndex, out var startOffset, out var endIndex, out var endOffset);
        var result = new StringBuilder();

        try
        {
            using var messages = tab.Messages.GetReadOnly(50);
            endIndex = Math.Min(endIndex, messages.Count - 1);
            for (var i = Math.Max(startIndex, 0); i <= endIndex; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(MessageText(messages[i]));
                var from = i == startIndex ? Math.Clamp(startOffset, 0, bytes.Length) : 0;
                var to = i == endIndex ? Math.Clamp(endOffset, 0, bytes.Length) : bytes.Length;
                if (result.Length > 0)
                    result.Append('\n');
                if (to > from)
                    result.Append(Encoding.UTF8.GetString(bytes, from, to - from));
            }
        }
        catch (ApplicationException)
        {
            // Couldn't get the message lock; nothing copied this time.
        }

        return result.ToString();
    }

    private static void Ordered(out int startIndex, out int startOffset, out int endIndex, out int endOffset)
    {
        var anchorFirst = AnchorIndex < FocusIndex || (AnchorIndex == FocusIndex && AnchorOffset <= FocusOffset);
        (startIndex, startOffset, endIndex, endOffset) = anchorFirst
            ? (AnchorIndex, AnchorOffset, FocusIndex, FocusOffset)
            : (FocusIndex, FocusOffset, AnchorIndex, AnchorOffset);
    }

    /// <summary>The byte boundary nearest to <paramref name="x"/> pixels into the text.</summary>
    private static unsafe int ByteAt(byte* text, byte* textEnd, float x)
    {
        var previous = 0;
        var previousWidth = 0f;
        var position = text;
        while (position < textEnd)
        {
            position = NextCharacter(position, textEnd);
            var width = Width(text, position);
            if (width >= x)
                return x - previousWidth < width - x ? previous : (int)(position - text);

            previous = (int)(position - text);
            previousWidth = width;
        }

        return (int)(textEnd - text);
    }

    private static unsafe byte* NextCharacter(byte* position, byte* end)
    {
        // Skip one UTF-8 character: the lead byte, then continuation bytes.
        position++;
        while (position < end && (*position & 0xC0) == 0x80)
            position++;
        return position;
    }

    private static unsafe float Width(byte* text, byte* textEnd)
    {
        if (textEnd <= text)
            return 0f;

        Vector2 size;
        ImGuiNative.CalcTextSize(&size, text, textEnd, 0, -1f);
        return size.X;
    }
}
