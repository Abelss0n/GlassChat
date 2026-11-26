using System.Numerics;
using GlassChat.GameFunctions.Types;

namespace GlassChat.Ui;

public interface IChatWindow
{
    Vector2 LastWindowPos { get; set; }
    Vector2 LastWindowSize { get; set; }
    HideState CurrentHideState { get; set; }
}