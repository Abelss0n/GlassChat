using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GlassChat.Util;

/// <summary>
/// Imports a Chat 2 configuration. GlassChat is a fork of Chat 2 and its
/// configuration has the same shape, but Dalamud saves configurations with
/// .NET type names embedded ("ChatTwo.Tab, ChatTwo"), so the file can't be
/// loaded as is: the type names are rewritten to GlassChat's first.
/// </summary>
public static partial class ChatTwoImport
{
    /// <summary>Chat 2's internal name, which Dalamud names its config file after.</summary>
    private const string ChatTwoInternalName = "ChatTwo";

    /// <summary>A "$type" property and its value.</summary>
    [GeneratedRegex("""("\$type"\s*:\s*")([^"]*)(")""")]
    private static partial Regex TypeProperty();

    /// <summary>"ChatTwo" as a whole name inside a type name (namespace or assembly).</summary>
    [GeneratedRegex(@"\bChatTwo\b")]
    private static partial Regex ChatTwoName();

    /// <summary>
    /// Chat 2's config file, next to GlassChat's own in Dalamud's plugin
    /// config folder, or null when Chat 2 has never been used on this PC.
    /// </summary>
    public static FileInfo? FindConfigFile()
    {
        var folder = Plugin.Interface.ConfigFile.DirectoryName;
        if (folder == null)
            return null;

        var file = new FileInfo(Path.Join(folder, $"{ChatTwoInternalName}.json"));
        return file.Exists ? file : null;
    }

    /// <summary>
    /// Reads a Chat 2 config file as a GlassChat configuration. Only type
    /// names change; every value, tab names included, is read as saved.
    /// </summary>
    /// <exception cref="Exception">The file couldn't be read or isn't a Chat 2 configuration.</exception>
    public static Configuration Load(string path)
    {
        var json = File.ReadAllText(path);
        json = TypeProperty().Replace(json, match => match.Groups[1].Value + ChatTwoName().Replace(match.Groups[2].Value, "GlassChat") + match.Groups[3].Value);

        // The same settings Dalamud loads plugin configurations with, plus a
        // binder that only allows the types a configuration is made of.
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Objects,
            TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            SerializationBinder = new ConfigurationBinder(),
        };

        return JsonConvert.DeserializeObject<Configuration>(json, settings)
               ?? throw new InvalidDataException("The file is empty.");
    }

    /// <summary>
    /// Replaces the current settings with an imported Chat 2 configuration,
    /// keeping GlassChat's own settings and the database state, then reloads
    /// what depends on them, as saving the settings window does.
    /// </summary>
    public static void Apply(Plugin plugin, Configuration imported)
    {
        var config = Plugin.Config;

        // GlassChat's own settings: Chat 2 has none of these, so the imported
        // configuration only holds their defaults. Keep this list in step with
        // the GlassChat fields in Configuration.
        var glassEnabled = config.GlassEnabled;
        var glassReducedQuality = config.GlassReducedQuality;
        var glassTheme = config.GlassTheme;
        var glassCustomFrost = config.GlassCustomFrost;
        var glassFrost = config.GlassFrost;
        var glassInputBar = config.GlassInputBar;
        var glassReducedMotion = config.GlassReducedMotion;
        var autoCollapse = config.ChatLogAutoCollapse;
        var collapsedPreview = config.ChatLogCollapsedPreview;
        var idleMinimise = config.ChatLogIdleMinimise;
        var idleMinimiseSeconds = config.ChatLogIdleMinimiseSeconds;

        // Describes GlassChat's own database, not a preference.
        var migrationStatus = config.MigrationStatus;

        var hideChatWasOn = config.HideChat;

        // backToOriginal closes the current popouts before their tabs go.
        config.UpdateFrom(imported, true);

        config.GlassEnabled = glassEnabled;
        config.GlassReducedQuality = glassReducedQuality;
        config.GlassTheme = glassTheme;
        config.GlassCustomFrost = glassCustomFrost;
        config.GlassFrost = glassFrost;
        config.GlassInputBar = glassInputBar;
        config.GlassReducedMotion = glassReducedMotion;
        config.ChatLogAutoCollapse = autoCollapse;
        config.ChatLogCollapsedPreview = collapsedPreview;
        config.ChatLogIdleMinimise = idleMinimise;
        config.ChatLogIdleMinimiseSeconds = idleMinimiseSeconds;
        config.MigrationStatus = migrationStatus;

        if (config.Tabs.Count == 0)
            config.Tabs.Add(TabsUtil.VanillaGeneral);

        plugin.WantedTab = 0;
        plugin.SaveConfig();

        plugin.MessageManager.ClearAllTabs();
        plugin.MessageManager.FilterAllTabsAsync();
        plugin.FontManager.BuildFonts();
        plugin.LanguageChanged(Plugin.Interface.UiLanguage);

        if (hideChatWasOn && !config.HideChat)
            GameFunctions.GameFunctions.SetChatInteractable(true);

        if (config.ShowEmotes)
            Task.Run(EmoteCache.LoadData);
    }

    /// <summary>
    /// Resolves the type names in a configuration file. Only GlassChat's own
    /// types, Dalamud's and .NET's core library are allowed, so a file can't
    /// make the importer create anything else.
    /// </summary>
    private sealed class ConfigurationBinder : ISerializationBinder
    {
        private static readonly Assembly GlassChat = typeof(Configuration).Assembly;
        private static readonly Assembly Dalamud = typeof(IDalamudPluginInterface).Assembly;
        private static readonly Assembly CoreLib = typeof(object).Assembly;

        public Type BindToType(string? assemblyName, string typeName)
        {
            var name = assemblyName == null ? typeName : $"{typeName}, {assemblyName}";
            return Type.GetType(name, ResolveAssembly, ResolveType, throwOnError: true)
                   ?? throw new JsonSerializationException($"Unknown type '{name}'.");
        }

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            assemblyName = serializedType.Assembly.GetName().Name;
            typeName = serializedType.FullName;
        }

        private static Assembly ResolveAssembly(AssemblyName name) => name.Name switch
        {
            "GlassChat" => GlassChat,
            "Dalamud" => Dalamud,
            "System.Private.CoreLib" => CoreLib,
            _ => throw new JsonSerializationException($"Types from '{name.Name}' aren't allowed in a configuration."),
        };

        private static Type? ResolveType(Assembly? assembly, string name, bool ignoreCase)
            => (assembly ?? CoreLib).GetType(name, false, ignoreCase);
    }
}
