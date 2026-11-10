using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.IoC;

namespace PCL.Plugin.NiButCrazy;

/// <summary>
/// 插件更新相关提示：
/// - 版本更新后首次启动弹窗展示 changelog.json 中当前版本的更新内容；
/// - 启动后异步检查插件市场 manifest，有新版本时弹 Toast。
/// </summary>
internal static class PluginUpdate
{
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/NiButCrazy/NiButCrazy-WonderfulFeatures/main/manifest.json";

    internal const string ReadmeUrl =
        "https://github.com/NiButCrazy/NiButCrazy-WonderfulFeatures/blob/main/README.md";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void Initialize()
    {
        try
        {
            Lifecycle.When(LifecycleState.WindowCreated, ScheduleChecks);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("注册更新检查失败：" + ex);
        }
    }

    private static void ScheduleChecks()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                // 窗口创建后稍等片刻，避开入场动画最集中的阶段。
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    ShowUpdateNotesIfNeeded();
                    _ = CheckForUpdatesAsync();
                };
                timer.Start();
            }));
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("安排更新检查失败：" + ex);
        }
    }

    private static void ShowUpdateNotesIfNeeded()
    {
        try
        {
            var current = NormalizeVersion(GetCurrentVersion());
            var statePath = Path.Combine(PluginSettings.DataDirectory, "last-version.txt");
            Directory.CreateDirectory(PluginSettings.DataDirectory);

            if (!File.Exists(statePath))
            {
                // 首次安装：只记录版本，不弹更新内容。
                File.WriteAllText(statePath, current);
                return;
            }

            var last = File.ReadAllText(statePath).Trim();
            if (string.Equals(last, current, StringComparison.Ordinal))
                return;

            // 先记录版本，保证关闭弹窗期间升级后不会在以后补弹旧内容。
            File.WriteAllText(statePath, current);

            if (!PluginSettings.Current.IsFeatureEnabled("updateNotes"))
                return;

            var notes = LoadChangelog(current);
            if (notes.Count == 0)
            {
                PluginBootstrap.WriteLog($"未找到版本 {current} 的更新内容");
                return;
            }

            var text = string.Join("\n",
                notes.Select(note => note.StartsWith("【", StringComparison.Ordinal) ? note : "· " + note));
            ModMain.MyMsgBox(text, $"NiButCrazy 奇妙特性 {current} 更新内容");
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("显示更新内容失败：" + ex);
        }
    }

    internal static Task CheckNowAsync() => CheckForUpdatesAsync(manual: true);

    private static async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (!manual && !PluginSettings.Current.IsFeatureEnabled("updateCheck"))
            return;

        try
        {
            var json = await Http.GetStringAsync(ManifestUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return;

            Version? latest = null;
            string? latestText = null;
            foreach (var entry in versions.EnumerateArray())
            {
                if (!entry.TryGetProperty("version", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                    continue;

                var text = value.GetString();
                if (text is null || !Version.TryParse(NormalizeVersion(text), out var parsed))
                    continue;

                if (latest is null || parsed > latest)
                {
                    latest = parsed;
                    latestText = text;
                }
            }

            if (latest is null || latestText is null)
                return;
            if (!Version.TryParse(NormalizeVersion(GetCurrentVersion()), out var current))
                return;
            if (latest <= current)
            {
                if (manual)
                    ModBase.RunInUi(() => HintService.Hint(
                        "NiButCrazy 奇妙特性已是最新版本", HintType.Success));
                return;
            }

            ModBase.RunInUi(() => HintService.Hint(
                $"NiButCrazy 奇妙特性 {latestText} 可用，可在插件市场更新",
                HintType.Info));
        }
        catch (Exception ex)
        {
            // 自动检查失败只记录；手动检查时给用户一个提示。
            PluginBootstrap.WriteLog("检查插件更新失败：" + ex.Message);
            if (manual)
                ModBase.RunInUi(() => HintService.Hint(
                    "检查插件更新失败，请检查网络", HintType.Error));
        }
    }

    private static List<string> LoadChangelog(string version)
    {
        var result = new List<string>();
        try
        {
            var path = Path.Combine(Paths.Plugins, PluginConstants.Id, "changelog.json");
            if (!File.Exists(path)) return result;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(version, out var notes))
                return result;

            if (notes.ValueKind == JsonValueKind.Array)
            {
                AddNotes(notes, result);
            }
            else if (notes.ValueKind == JsonValueKind.Object)
            {
                foreach (var section in notes.EnumerateObject())
                {
                    if (section.Value.ValueKind != JsonValueKind.Array) continue;
                    var items = new List<string>();
                    AddNotes(section.Value, items);
                    if (items.Count == 0) continue;
                    result.Add($"【{section.Name}】");
                    result.AddRange(items);
                }
            }
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("读取更新内容失败：" + ex.Message);
        }

        return result;
    }

    private static void AddNotes(JsonElement array, List<string> result)
    {
        foreach (var note in array.EnumerateArray())
            if (note.ValueKind == JsonValueKind.String)
            {
                var text = note.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }
    }

    private static string GetCurrentVersion()
    {
        try
        {
            var path = Path.Combine(Paths.Plugins, PluginConstants.Id, "plugin.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out var value) &&
                value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "0.0.0";
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("读取插件版本失败：" + ex.Message);
        }

        return "0.0.0";
    }

    private static string NormalizeVersion(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
    }
}
