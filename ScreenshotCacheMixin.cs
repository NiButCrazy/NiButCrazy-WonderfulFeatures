using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 截图列表页面缓存：从其他页面返回时，如果截图目录没有变化，
/// 直接复用已有卡片和图片，不重新扫描目录、不重建列表。
/// </summary>
[Mixin("PCL.PageInstanceScreenshot", Optional = true)]
internal static class ScreenshotCacheMixin
{
    private sealed class CacheState
    {
        public bool Valid;
        public string? Folder;
        public long LastWriteTicks;
        public int FileCount;
    }

    private static readonly ConditionalWeakTable<PageInstanceScreenshot, CacheState> States = new();

    [Inject("PageSetupLaunch_Loaded", At = MixinAt.Head, Cancellable = true)]
    private static void BeforeLoaded([This] PageInstanceScreenshot page, CallbackInfo callback)
    {
        try
        {
            var folder = GetScreenshotFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;

            var state = States.GetOrCreateValue(page);
            var directory = new DirectoryInfo(folder);
            var ticks = directory.Exists ? directory.LastWriteTimeUtc.Ticks : 0;
            var count = directory.Exists ? Directory.GetFiles(folder).Length : 0;

            if (state.Valid &&
                string.Equals(state.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                state.LastWriteTicks == ticks &&
                state.FileCount == count &&
                page.FindName("PanList") is Panel panList &&
                panList.Children.Count > 0)
            {
                if (page.FindName("PanBack") is MyScrollViewer panBack)
                    panBack.ScrollToHome();
                callback.Cancel();
                return;
            }

            state.Folder = folder;
            state.LastWriteTicks = ticks;
            state.FileCount = count;
            state.Valid = true;
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("截图列表缓存判断失败：" + ex);
        }
    }

    private static string? GetScreenshotFolder()
    {
        try
        {
            var instance = PageInstanceLeft.McInstance;
            return instance is null ? null : Path.Combine(instance.PathIndie, "screenshots");
        }
        catch
        {
            return null;
        }
    }
}
