using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using PCL.Core.App;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy;

internal static class PluginConstants
{
    public const string Id = "nibutcrazy.wonderful-features";
    public const string Name = "NiButCrazy的奇妙特性";
}

/// <summary>
/// 插件设置。首次运行时会自动创建在 PCL/Plugins/data/&lt;插件 ID&gt;/config.json。
/// </summary>
internal sealed class PluginSettings
{
    public int AnimationFps { get; set; } = 240;

    public bool VariedDefaultSkins { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static PluginSettings? _current;
    private static readonly object SyncRoot = new();

    public static PluginSettings Current
    {
        get
        {
            lock (SyncRoot) return _current ??= Load();
        }
    }

    public static string DataDirectory => Path.Combine(Paths.Plugins, "data", PluginConstants.Id);

    public static string SkinsDirectory => Path.Combine(DataDirectory, "Skins");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static string LogPath => Path.Combine(DataDirectory, "plugin.log");

    private static PluginSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(SkinsDirectory);
            if (!File.Exists(ConfigPath))
            {
                var defaults = new PluginSettings();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(defaults, JsonOptions));
                return defaults;
            }

            var text = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<PluginSettings>(text, JsonOptions) ?? new PluginSettings();
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("读取配置失败，使用默认值：" + ex);
            return new PluginSettings();
        }
    }
}

/// <summary>
/// Mixin 配置处理器。插件没有通用生命周期入口，因此在这里完成一次性初始化。
/// </summary>
public sealed class NiButCrazyConfigPlugin : IMixinConfigPlugin
{
    private static int _initialized;

    public void PreApply(MixinApplyContext context)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        PluginBootstrap.Initialize();
    }
}

internal static class PluginBootstrap
{
    private static readonly object LogLock = new();

    public static void Initialize()
    {
        try
        {
            var settings = PluginSettings.Current;
            EnsureSkinReadme();
            ApplyAnimationFps(settings.AnimationFps);
            WriteLog($"初始化完成：AnimationFps={settings.AnimationFps}, VariedDefaultSkins={settings.VariedDefaultSkins}");
        }
        catch (Exception ex)
        {
            WriteLog("初始化失败：" + ex);
        }
    }

    private static void EnsureSkinReadme()
    {
        var readmePath = Path.Combine(PluginSettings.SkinsDirectory, "说明.txt");
        if (File.Exists(readmePath)) return;
        File.WriteAllText(readmePath,
            "把 Minecraft 皮肤 PNG（64x64 或 64x32）放到这个目录，文件名使用玩家 ID。\r\n" +
            "例如：Skins\\NiButCrazy.png\r\n" +
            "也可以用离线 UUID 命名，或使用 default.png 作为所有离线账号的默认皮肤。\r\n" +
            "重新打开启动器或刷新皮肤后生效。\r\n");
    }

    /// <summary>
    /// 通过反射写入动画帧率配置。PCL.Core 的配置组类型是 internal，
    /// 但公开属性仍然可以通过反射访问。
    /// </summary>
    private static void ApplyAnimationFps(int target)
    {
        if (target <= 0) return;

        var systemField = typeof(Config).GetField(
            "System",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var group = systemField?.GetValue(null);
        if (group is null)
        {
            WriteLog("未找到 Config.System，跳过动画帧率设置");
            return;
        }

        var fpsProperty = group.GetType().GetProperty(
            "AnimationFpsLimit",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (fpsProperty is null || !fpsProperty.CanWrite)
        {
            WriteLog("未找到可写的 AnimationFpsLimit，跳过动画帧率设置");
            return;
        }

        var current = fpsProperty.GetValue(group) is int value ? value : 0;
        if (current >= target)
        {
            WriteLog($"当前动画帧率配置已为 {current}，无需修改");
            return;
        }

        fpsProperty.SetValue(group, target);
        WriteLog($"动画帧率配置已设置为 {target}");
    }

    public static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(PluginSettings.DataDirectory);
            lock (LogLock)
                File.AppendAllText(PluginSettings.LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能影响插件本身。
        }
    }
}
