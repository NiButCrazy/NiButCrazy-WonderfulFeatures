using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy;

internal static class PluginConstants
{
    public const string Id = "nibutcrazy.wonderful-features";
    public const string Name = "NiButCrazy 奇妙特性";
}

/// <summary>
/// 插件设置。首次运行时会自动创建在 PCL/Plugins/data/&lt;插件 ID&gt;/config.json。
/// </summary>
internal sealed class PluginSettings
{
    public bool VariedDefaultSkins { get; set; } = true;

    /// <summary>
    /// 资源管理页记住的排序方式，1 = 资源名（PCL 默认）。
    /// </summary>
    public int CompSortMethod { get; set; } = 1;

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

    /// <summary>
    /// 注意必须使用单数 Skin：MySkin.Load 会用 "Skin/" 截取头像缓存 ID，
    /// 使用 "Skins/" 会让它把整条绝对路径当作 ID，从而把头像覆盖到皮肤文件本身。
    /// </summary>
    public static string SkinDirectory => Path.Combine(DataDirectory, "Skin");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static string LogPath => Path.Combine(DataDirectory, "plugin.log");

    public static void Save()
    {
        lock (SyncRoot)
        {
            if (_current is null) return;
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_current, JsonOptions));
            }
            catch (Exception ex)
            {
                PluginBootstrap.WriteLog("保存配置失败：" + ex);
            }
        }
    }

    private static PluginSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(SkinDirectory);
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
    private static long _startupTicks;

    /// <summary>
    /// 插件加载后的短时间内视为启动阶段，用于让出窗口入场动画。
    /// </summary>
    public static bool IsStartupWindow => Environment.TickCount64 - _startupTicks < 2000;

    public static void Initialize()
    {
        try
        {
            _startupTicks = Environment.TickCount64;
            var settings = PluginSettings.Current;
            MigrateLegacySkinDirectory();
            EnsureSkinReadme();
            WriteLog($"初始化完成：VariedDefaultSkins={settings.VariedDefaultSkins}");
        }
        catch (Exception ex)
        {
            WriteLog("初始化失败：" + ex);
        }
    }

    private static void MigrateLegacySkinDirectory()
    {
        var legacy = Path.Combine(PluginSettings.DataDirectory, "Skins");
        if (!Directory.Exists(legacy)) return;

        var target = Path.Combine(
            PluginSettings.DataDirectory,
            "Skins.old-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        try
        {
            Directory.Move(legacy, target);
            WriteLog($"旧皮肤目录已重命名：{target}（请重新选择皮肤）");
        }
        catch (Exception ex)
        {
            WriteLog("重命名旧皮肤目录失败：" + ex);
        }
    }

    private static void EnsureSkinReadme()
    {
        var readmePath = Path.Combine(PluginSettings.SkinDirectory, "说明.txt");
        if (File.Exists(readmePath)) return;
        File.WriteAllText(readmePath,
            "把 Minecraft 皮肤 PNG（64x64 或 64x32）放到这个目录，文件名使用玩家 ID。\r\n" +
            "例如：Skin\\NiButCrazy.png\r\n" +
            "也可以用离线 UUID 命名，或使用 default.png 作为所有离线账号的默认皮肤。\r\n" +
            "重新打开启动器或刷新皮肤后生效。\r\n");
    }

    /// <summary>
    /// 把用户选择的皮肤复制到插件皮肤目录，并按玩家 ID 命名。
    /// </summary>
    public static void ApplyOfflineSkin(ModProfile.McProfile profile, string sourcePath)
    {
        try
        {
            using (var stream = File.OpenRead(sourcePath))
            {
                var frame = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
                if (frame.PixelWidth < 64 || frame.PixelHeight < 32)
                    throw new InvalidDataException(
                        $"皮肤尺寸过小（{frame.PixelWidth}×{frame.PixelHeight}），请选择 64×64 或 64×32 的 PNG。");
            }

            Directory.CreateDirectory(PluginSettings.SkinDirectory);
            var fileName = SanitizeFileName(profile.Username) + ".png";
            var target = Path.Combine(PluginSettings.SkinDirectory, fileName);
            File.Copy(sourcePath, target, overwrite: true);
            WriteLog($"离线皮肤已更新：{profile.Username} -> {target}");

            HintService.Hint("离线皮肤已更新", HintType.Success);
            ModMain.frmLoginProfileSkin?.Reload();
        }
        catch (Exception ex)
        {
            WriteLog("应用离线皮肤失败：" + ex);
            HintService.Hint("应用皮肤失败：" + ex.Message, HintType.Error);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = (name ?? string.Empty)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        var result = new string(characters).Trim();
        return string.IsNullOrWhiteSpace(result) ? "default" : result;
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
