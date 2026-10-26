using System;
using System.Reflection;
using System.Threading;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>跨 Mixin 共享的临时状态。</summary>
internal static class FeatureState
{
    /// <summary>点击「下载新资源」时带走的搜索词。</summary>
    public static string? PendingDownloadSearch;

    /// <summary>搜索词对应的下载页类型（Mod / 资源包 / 光影）。</summary>
    public static int? PendingDownloadPageType;
}

/// <summary>
/// 本地资源管理页点击「下载新资源」时，记录当前搜索框内容，
/// 供下载页加载时自动填入并搜索。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class DownloadSearchMixin
{
    private static readonly FieldInfo? SearchBoxField = typeof(PageInstanceCompResource).GetField(
        "SearchBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? CompTypeField = typeof(PageInstanceCompResource).GetField(
        "currentCompType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Inject("BtnManageDownload_Click", At = MixinAt.Head)]
    private static void CaptureSearch([This] PageInstanceCompResource instance)
    {
        try
        {
            var searchBox = SearchBoxField?.GetValue(instance) as MySearchBox;
            var text = searchBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            FeatureState.PendingDownloadSearch = text;
            FeatureState.PendingDownloadPageType = CompTypeField?.GetValue(instance) is { } compType
                ? Convert.ToInt32(compType)
                : null;
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("捕获下载搜索词失败：" + ex);
        }
    }
}

/// <summary>
/// 下载页加载完成后，把待处理搜索词填入搜索框并触发搜索。
/// </summary>
[Mixin("PCL.PageComp")]
internal static class DownloadPageMixin
{
    private static readonly FieldInfo? SearchBoxField = typeof(PageComp).GetField(
        "PanSearchBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? StartNewSearchMethod = typeof(PageComp).GetMethod(
        "StartNewSearch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Inject("PageCompControls_Inited", At = MixinAt.Tail)]
    private static void ApplySearch([This] PageComp instance)
    {
        var text = FeatureState.PendingDownloadSearch;
        if (string.IsNullOrWhiteSpace(text)) return;

        // 只让目标类型（Mod / 资源包 / 光影）的下载页消费该搜索词
        var pendingType = FeatureState.PendingDownloadPageType;
        if (pendingType is not null && Convert.ToInt32(instance.PageType) != pendingType.Value) return;

        FeatureState.PendingDownloadSearch = null;
        FeatureState.PendingDownloadPageType = null;

        try
        {
            var searchBox = SearchBoxField?.GetValue(instance) as MySearchBox;
            if (searchBox is null)
            {
                PluginBootstrap.WriteLog("下载页未找到搜索框，跳过搜索词填充");
                return;
            }

            searchBox.Text = text;
            StartNewSearchMethod?.Invoke(instance, null);
            PluginBootstrap.WriteLog($"下载页已应用搜索词：{text}");
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("应用下载搜索词失败：" + ex);
        }
    }
}

/// <summary>
/// 记住资源管理的排序方式：
/// - 用户在排序菜单里选择后写入插件配置；
/// - 页面加载把排序重置为「资源名」时，自动恢复上次选择。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class SortMemoryMixin
{
    private const int CompNameSort = 1;
    private const long UserSelectionWindowMs = 15_000;

    private static readonly FieldInfo? SortField = typeof(PageInstanceCompResource).GetField(
        "currentSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? SetSortMethodMethod = typeof(PageInstanceCompResource).GetMethod(
        "SetSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static long _sortMenuTicks;
    private static int _restoring;

    [Inject("BtnSortClick", At = MixinAt.Head)]
    private static void MarkSortMenuOpened()
    {
        _sortMenuTicks = Environment.TickCount64;
    }

    [Inject("SetSortMethod", At = MixinAt.Tail)]
    private static void RememberSort([This] PageInstanceCompResource instance)
    {
        if (Interlocked.Exchange(ref _restoring, 0) != 0) return;

        try
        {
            var sortField = SortField;
            if (sortField is null) return;

            var current = Convert.ToInt32(sortField.GetValue(instance));
            var settings = PluginSettings.Current;
            var saved = settings.CompSortMethod;
            var isUserSelection = Environment.TickCount64 - _sortMenuTicks <= UserSelectionWindowMs;
            _sortMenuTicks = 0;

            if (isUserSelection)
            {
                if (current != saved)
                {
                    settings.CompSortMethod = current;
                    PluginSettings.Save();
                    PluginBootstrap.WriteLog($"已记住资源排序方式：{current}");
                }
                return;
            }

            // 页面加载会调用 SetSortMethod(CompName)，此时恢复用户保存的排序。
            if (current != CompNameSort || saved == CompNameSort) return;
            if (!Enum.IsDefined(sortField.FieldType, saved)) return;

            Interlocked.Exchange(ref _restoring, 1);
            SetSortMethodMethod?.Invoke(instance, [Enum.ToObject(sortField.FieldType, saved)]);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _restoring, 0);
            PluginBootstrap.WriteLog("排序记忆失败：" + ex);
        }
    }
}
