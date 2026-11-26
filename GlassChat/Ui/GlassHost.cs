using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Glasswork;
using Glasswork.Backend;
using Glasswork.Effects;
using Glasswork.Glass;

namespace GlassChat.Ui;

/// <summary>
/// Owns the Glasswork glass layer, the per-frame Glasswork context and the
/// selected theme (a Glasswork Look). Everything here runs on the draw thread
/// and allocates nothing per frame; a theme change rebuilds its caches once.
/// </summary>
public sealed class GlassHost : IDisposable
{
    /// <summary>
    /// Tint for themes whose panels are solid rather than glass, so the glass
    /// still takes on the theme's background colour.
    /// </summary>
    private const float SolidThemeTint = 0.35f;

    /// <summary>The ImGui colours a theme replaces, in push order.</summary>
    private static readonly ImGuiCol[] ThemeSlots =
    [
        ImGuiCol.Text, ImGuiCol.TextDisabled,
        ImGuiCol.WindowBg, ImGuiCol.ChildBg, ImGuiCol.PopupBg, ImGuiCol.Border,
        ImGuiCol.FrameBg, ImGuiCol.FrameBgHovered, ImGuiCol.FrameBgActive,
        ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.TitleBgCollapsed,
        ImGuiCol.ScrollbarBg, ImGuiCol.ScrollbarGrab, ImGuiCol.ScrollbarGrabHovered, ImGuiCol.ScrollbarGrabActive,
        ImGuiCol.Button, ImGuiCol.ButtonHovered, ImGuiCol.ButtonActive,
        ImGuiCol.Header, ImGuiCol.HeaderHovered, ImGuiCol.HeaderActive,
        ImGuiCol.Separator, ImGuiCol.SeparatorHovered, ImGuiCol.SeparatorActive,
        ImGuiCol.ResizeGrip, ImGuiCol.ResizeGripHovered, ImGuiCol.ResizeGripActive,
        ImGuiCol.Tab, ImGuiCol.TabHovered, ImGuiCol.TabActive, ImGuiCol.TabUnfocused, ImGuiCol.TabUnfocusedActive,
        ImGuiCol.TableBorderStrong, ImGuiCol.TableBorderLight,
        ImGuiCol.TextSelectedBg, ImGuiCol.NavHighlight,
    ];

    private static string[]? ThemeKeysCache;
    private static string[]? ThemeNamesCache;

    private readonly IDalamudPluginInterface Interface;
    private readonly GlassLayer Layer;
    private readonly ImGuiFontSource Fonts = new();
    private readonly uint[] ThemeColours = new uint[ThemeSlots.Length];

    private Look CurrentLook = Look.NeutralDark();
    private string? LoadedTheme;
    private bool ThemeActive;

    private GlowSpec FocusGlow = new() { Radius = 10f, Intensity = 0.55f, PulseHertz = 0f };

    /// <summary>
    /// A breathing glow in the theme's accent (a transparent colour takes the
    /// frame Look's accent), for things that want attention: unread tabs and
    /// the minimised bar's expand button. Holds still under reduced motion.
    /// </summary>
    private GlowSpec AttentionGlow = new() { Color = Color.Transparent, Radius = 7f, Intensity = 0.6f, PulseHertz = 0.6f };

    // Cached materials at the current UI scale, rebuilt only when their
    // inputs change. Opacity is applied per draw on a copy.
    private GlassMaterial WindowMaterial;
    private GlassMaterial BarMaterial;
    private float MaterialScale = -1f;
    private bool MaterialCustomFrost;
    private float MaterialFrost = -1f;

    private bool FrameOpen;

    public GlassHost(IDalamudPluginInterface pluginInterface)
    {
        Interface = pluginInterface;
        Layer = new GlassLayer(() => Interface.UiBuilder.DeviceHandle, OnLog);
        Interface.UiBuilder.ResizeBuffers += Layer.ReleaseResources;
    }

    /// <summary>True when the user turned glass on and the layer hasn't faulted.</summary>
    public bool Enabled => Plugin.Config.GlassEnabled && !Layer.Faulted;

    /// <summary>
    /// The keys of the selectable themes: Glasswork's built-in dark Looks.
    /// Light Looks are left out because chat channel colours are tuned for a
    /// dark background.
    /// </summary>
    public static ReadOnlySpan<string> ThemeKeys
    {
        get
        {
            EnsureThemeList();
            return ThemeKeysCache!;
        }
    }

    /// <summary>Display names matching <see cref="ThemeKeys"/>.</summary>
    public static ReadOnlySpan<string> ThemeNames
    {
        get
        {
            EnsureThemeList();
            return ThemeNamesCache!;
        }
    }

    /// <summary>The display name of a theme key, or null for the default/unknown.</summary>
    public static string? ThemeName(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        var keys = ThemeKeys;
        for (var i = 0; i < keys.Length; i++)
            if (keys[i] == key)
                return ThemeNames[i];

        return null;
    }

    /// <summary>
    /// Call first thing in the plugin's draw handler, before any window draws.
    /// </summary>
    public void BeginFrame()
    {
        var theme = Plugin.Config.GlassTheme;
        if (!string.Equals(theme, LoadedTheme, StringComparison.Ordinal))
            LoadTheme(theme);

        var reducedMotion = Plugin.Config.GlassReducedMotion;
        Layer.MaxQuality = Plugin.Config.GlassReducedQuality ? GlassQuality.Reduced : GlassQuality.Full;
        Layer.ReducedMotion = reducedMotion;
        Layer.PointerLight = !reducedMotion;
        CurrentLook.ReduceMotion = reducedMotion;
        Layer.BeginFrame();

        if (!Enabled)
            return;

        Frame.Begin(CurrentLook, Fonts, Layer, ImGuiHelpers.GlobalScale, ImGui.GetIO().DeltaTime, ImGui.GetFrameCount());
        FrameOpen = true;
    }

    /// <summary>Call last thing in the plugin's draw handler.</summary>
    public void EndFrame()
    {
        if (!FrameOpen)
            return;

        Frame.End();
        FrameOpen = false;
    }

    /// <summary>
    /// Whether a window should drop its ImGui background in favour of glass.
    /// Glass only runs on the main viewport, and a docked window shares its
    /// node's background, so both cases keep the plain ImGui background.
    /// </summary>
    public bool WantsWindowGlass(bool onMainViewport, bool docked)
        => Enabled && onMainViewport && !docked;

    /// <summary>
    /// Pushes the selected theme's ImGui colours. Call before the window
    /// begins and pass the result to <see cref="PopTheme"/> after it ends.
    /// </summary>
    /// <returns>How many colours were pushed; 0 without glass or a theme.</returns>
    public int PushTheme()
    {
        if (!Enabled || !ThemeActive)
            return 0;

        for (var i = 0; i < ThemeSlots.Length; i++)
            ImGui.PushStyleColor(ThemeSlots[i], ThemeColours[i]);

        return ThemeSlots.Length;
    }

    public static void PopTheme(int count)
    {
        if (count > 0)
            ImGui.PopStyleColor(count);
    }

    /// <summary>
    /// Draws glass behind the current window. Call right after the window
    /// begins, only for windows drawn with <see cref="ImGuiWindowFlags.NoBackground"/>.
    /// Where glass can't record this frame, Glasswork draws a flat fill in the
    /// same place instead.
    /// </summary>
    public void DrawWindowBackground(float opacity)
    {
        EnsureMaterials();
        var material = WindowMaterial with { Opacity = opacity };
        Layer.DrawWindowBackground(in material, ImGui.GetStyle().WindowRounding);
    }

    /// <summary>
    /// A thin glass strip behind a row of controls, drawn before them, with
    /// the given corner radius. Where glass can't record, nothing is drawn
    /// and the row looks as it always did.
    /// </summary>
    public void DrawBarGlass(ImDrawListPtr drawList, Vector2 min, Vector2 max, float radius)
    {
        if (!FrameOpen || !Plugin.Config.GlassInputBar || !Layer.CanRecord())
            return;

        EnsureMaterials();
        var shape = GlassShape.FromRect(new Rect(min, max), radius);
        Layer.TryDraw(drawList, in shape, in BarMaterial);
    }

    /// <summary>
    /// A breathing glow in the theme's accent around a rounded rect, drawn
    /// before the item it surrounds.
    /// </summary>
    public void DrawAttentionGlow(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        if (!FrameOpen)
            return;

        Paint.Glow(drawList, new Rect(min, max), Corners.All(rounding), CornerStyle.Continuous, in AttentionGlow, ImGuiHelpers.GlobalScale, Frame.Time);
    }

    /// <summary>
    /// A soft glow around a rounded rect, drawn before the item it surrounds.
    /// </summary>
    public void DrawFocusGlow(ImDrawListPtr drawList, Vector2 min, Vector2 max, uint abgr)
    {
        if (!FrameOpen)
            return;

        FocusGlow.Color = Color.FromAbgr(abgr);
        Paint.Glow(drawList, new Rect(min, max), Corners.All(ImGui.GetStyle().FrameRounding), CornerStyle.Continuous, in FocusGlow, ImGuiHelpers.GlobalScale, Frame.Time);
    }

    public void Dispose()
    {
        Interface.UiBuilder.ResizeBuffers -= Layer.ReleaseResources;
        Layer.Dispose();
    }

    private void EnsureMaterials()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var customFrost = Plugin.Config.GlassCustomFrost;
        var frost = Plugin.Config.GlassFrost;
        if (scale == MaterialScale && customFrost == MaterialCustomFrost && frost == MaterialFrost)
            return;

        MaterialScale = scale;
        MaterialCustomFrost = customFrost;
        MaterialFrost = frost;

        WindowMaterial = BaseWindowMaterial(customFrost ? Math.Clamp(frost, 0f, 1f) : null).Scaled(scale);
        BarMaterial = GlassMaterial.Thin.Scaled(scale);
    }

    private GlassMaterial BaseWindowMaterial(float? frost)
    {
        if (!ThemeActive)
            return frost is { } amount ? GlassMaterial.Regular.WithAmount(amount) : GlassMaterial.Regular;

        // Frosted themes carry their own tint (Clear Blue's navy, Eorzea's
        // smoked slate); solid ones tint toward their window background.
        var panel = CurrentLook.PanelMaterial;
        var tinted = panel.TintAmount > 0f;
        var tint = (tinted ? panel.Tint : CurrentLook.SecondaryBackground).WithAlpha(1f);
        var tintAmount = tinted ? panel.TintAmount : SolidThemeTint;

        return GlassMaterial.Tinted(tint, tintAmount, CurrentLook.IsLight)
            .WithStyle(in CurrentLook.Glass)
            .WithAmount(frost ?? panel.Amount ?? CurrentLook.GlassAmount);
    }

    private void LoadTheme(string? key)
    {
        LoadedTheme = key;
        ThemeActive = false;
        CurrentLook = Look.NeutralDark();

        if (!string.IsNullOrEmpty(key) && Look.TryFromName(key, out var look) && look != null)
        {
            CurrentLook = look;
            ThemeActive = true;
            BuildThemeColours(look);
        }

        // Force the materials to rebuild with the new theme.
        MaterialScale = -1f;
    }

    private void BuildThemeColours(Look look)
    {
        var accent = look.Accent;
        var border = look.PanelBorder.IsVisible ? look.PanelBorder : look.Separator;

        Color[] colours =
        [
            look.Label, look.SecondaryLabel,
            look.Background, Color.Transparent, look.Background.WithAlpha(0.97f), border,
            look.Fill, look.Fill.Lighten(0.08f), look.Fill.Lighten(0.15f),
            look.Background, look.SecondaryBackground.WithAlpha(1f), look.Background,
            Color.Transparent, look.Fill, accent.WithAlpha(0.5f), accent.WithAlpha(0.7f),
            look.Fill, accent.WithAlpha(0.35f), accent.WithAlpha(0.55f),
            accent.WithAlpha(0.3f), accent.WithAlpha(0.4f), accent.WithAlpha(0.55f),
            look.Separator, accent.WithAlpha(0.6f), accent,
            accent.WithAlpha(0.2f), accent.WithAlpha(0.5f), accent.WithAlpha(0.75f),
            look.Fill, accent.WithAlpha(0.45f), accent.WithAlpha(0.6f), look.Fill, accent.WithAlpha(0.45f),
            look.Separator, look.Separator,
            accent.WithAlpha(0.35f), accent,
        ];

        for (var i = 0; i < ThemeSlots.Length; i++)
            ThemeColours[i] = colours[i].ToAbgr();
    }

    private static void EnsureThemeList()
    {
        if (ThemeKeysCache != null)
            return;

        var keys = new List<string>();
        var names = new List<string>();
        foreach (var key in Look.BuiltInNames)
        {
            if (!Look.TryFromName(key, out var look) || look == null || look.IsLight)
                continue;

            keys.Add(key);
            names.Add(look.Name);
        }

        ThemeKeysCache = keys.ToArray();
        ThemeNamesCache = names.ToArray();
    }

    private static void OnLog(string message, Exception? exception)
    {
        if (exception != null)
            Plugin.Log.Error(exception, $"[Glasswork] {message}");
        else
            Plugin.Log.Debug($"[Glasswork] {message}");
    }
}
