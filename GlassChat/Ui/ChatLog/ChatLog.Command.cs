using GlassChat.GameFunctions.Types;

namespace GlassChat.Ui.ChatLog;

public partial class ChatLog
{
    private void ToggleChat(string _, string arguments)
    {
        switch (arguments)
        {
            case "hide":
                CurrentHideState = HideState.User;
                break;
            case "show":
                CurrentHideState = HideState.None;
                break;
            case "toggle":
                CurrentHideState = CurrentHideState switch
                {
                    HideState.User or HideState.CutsceneOverride => HideState.None,
                    HideState.Cutscene => HideState.CutsceneOverride,
                    HideState.None => HideState.User,
                    _ => CurrentHideState,
                };
                break;
        }
    }

    /// <summary>
    /// Dalamud's "open main window" action (the Open button in the plugin
    /// installer). The chat window is always open, so this brings it back if
    /// the user hid it (or a cutscene did) and expands it if it's minimised.
    /// </summary>
    private void OpenMainUi()
    {
        CurrentHideState = CurrentHideState switch
        {
            HideState.User => HideState.None,
            HideState.Cutscene => HideState.CutsceneOverride,
            _ => CurrentHideState,
        };

        // Counts as activity, so "hide when inactive" doesn't hide it again
        // straight away.
        InputHandler.LastActivityTime = Environment.TickCount64;

        ExpandedForChat = false;
        SetCollapsed(false);
    }

    private void ClearLog(string command, string arguments)
    {
        switch (arguments)
        {
            case "all":
                Plugin.MessageManager.ClearAllTabs();
                break;
            case "help":
                Plugin.ChatGui.Print("- /glasschatclear: clears the active tab's log");
                Plugin.ChatGui.Print("- /glasschatclear all: clears all tabs' logs and the global history");
                Plugin.ChatGui.Print("- /glasschatclear help: shows this help");
                break;
            default:
                if (Plugin.LastTab > -1 && Plugin.LastTab < Plugin.Config.Tabs.Count)
                    Plugin.Config.Tabs[Plugin.LastTab].Clear();
                break;
        }
    }
}