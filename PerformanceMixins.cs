using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 修复本地资源搜索的 CancellationTokenSource 释放竞态：
/// 原实现会 Dispose 旧的 CTS，但旧的异步任务仍在访问其 Token，
/// 导致日志里反复出现 ObjectDisposedException。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class SearchCancellationFixMixin
{
    private static readonly FieldInfo? CancelTokenField = typeof(PageInstanceCompResource).GetField(
        "_cancelToken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? SearchResultField = typeof(PageInstanceCompResource).GetField(
        "searchResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? SearchBoxField = typeof(PageInstanceCompResource).GetField(
        "SearchBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? GetSearchResultMethod = typeof(PageInstanceCompResource).GetMethod(
        "GetSearchResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Overwrite("SearchRun")]
    private static void SearchRun(
        [This] PageInstanceCompResource instance,
        [Arg(0)] object sender,
        [Arg(1)] EventArgs e)
    {
        var currentToken = new CancellationTokenSource();
        var oldToken = CancelTokenField?.GetValue(instance) as CancellationTokenSource;
        CancelTokenField?.SetValue(instance, currentToken);

        try
        {
            oldToken?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 旧令牌已经被释放时忽略。
        }
        // 注意不要 Dispose 旧 CTS：此前的异步任务可能仍在读取它的 Token。

        var token = currentToken.Token;
        instance.Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;

                if (instance.IsSearching)
                {
                    var searchText = (SearchBoxField?.GetValue(instance) as MySearchBox)?.Text ?? string.Empty;
                    var result = await Task.Run(
                        () => GetSearchResultMethod?.Invoke(instance, [searchText]), token);
                    if (result is not null)
                        SearchResultField?.SetValue(instance, result);
                }

                if (token.IsCancellationRequested) return;
                instance.RefreshUI();
            }
            catch (TaskCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "搜索过程中发生异常");
            }
        }));
    }
}

/// <summary>
/// Mod 列表加载优化：原实现会在 UI 线程一次性创建全部 Mod 控件，
/// 数量多时会长时间阻塞渲染。这里改为每帧创建一小批，避免动画卡死。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class ModListLoadMixin
{
    private const int BatchSize = 12;

    private static readonly FieldInfo? PanBackField = FindField("PanBack");
    private static readonly FieldInfo? PanEmptyField = FindField("PanEmpty");
    private static readonly FieldInfo? PanSchematicEmptyField = FindField("PanSchematicEmpty");
    private static readonly FieldInfo? EmptyTitleField = FindField("TxtEmptyTitle");
    private static readonly FieldInfo? EmptyDescriptionField = FindField("TxtEmptyDescription");
    private static readonly FieldInfo? HintBackField = FindField("BtnHintBack");
    private static readonly FieldInfo? SearchBoxField = FindField("SearchBox");
    private static readonly FieldInfo? CompTypeField = FindField("currentCompType");

    private static readonly MethodInfo? BuildItemMethod = typeof(PageInstanceCompResource).GetMethod(
        "BuildLocalCompItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? GetCompFolderMethod = typeof(PageInstanceCompResource).GetMethod(
        "GetCompFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? SetSortMethodMethod = typeof(PageInstanceCompResource).GetMethod(
        "SetSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly Type? SortMethodType = typeof(PageInstanceCompResource).GetNestedType(
        "SortMethod", BindingFlags.Public | BindingFlags.NonPublic);

    private static int _generation;

    private static FieldInfo? FindField(string name) => typeof(PageInstanceCompResource).GetField(
        name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Overwrite("LoadUIFromLoaderOutput")]
    private static void LoadUIFromLoaderOutput([This] PageInstanceCompResource instance)
    {
        try
        {
            var output = ModLocalComp.compResourceListLoader.output;
            if (output.Count == 0)
            {
                ShowEmptyState(instance);
                return;
            }

            SetVisibility(PanBackField, instance, Visibility.Visible);
            SetVisibility(PanEmptyField, instance, Visibility.Collapsed);
            SetVisibility(PanSchematicEmptyField, instance, Visibility.Collapsed);

            instance.modItems.Clear();
            var compType = CompTypeField?.GetValue(instance);
            var rootPath = Path.TrimEndingDirectorySeparator(
                GetCompFolderMethod?.Invoke(null, [PageInstanceLeft.McInstance, compType]) as string ?? string.Empty);
            var currentFolderPath = instance.CurrentFolderPath;

            var itemsToShow = output.Where(item =>
            {
                var itemPath = item.IsFolder ? item.ActualPath : item.path;
                var parentDirectory = Directory.GetParent(itemPath)?.FullName;
                if (string.IsNullOrEmpty(currentFolderPath))
                    return parentDirectory is not null && FileSystemPath.Equals(parentDirectory, rootPath);
                return parentDirectory is not null && FileSystemPath.Equals(parentDirectory, currentFolderPath);
            }).ToList();

            var generation = Interlocked.Increment(ref _generation);
            var index = 0;

            void Finish()
            {
                if (generation != _generation || instance.Dispatcher.HasShutdownStarted) return;

                instance.Filter = PageInstanceCompResource.FilterType.All;
                if (SearchBoxField?.GetValue(instance) is MySearchBox searchBox)
                    searchBox.Text = string.Empty;
                instance.RefreshUI();

                if (SortMethodType is not null && SetSortMethodMethod is not null)
                    SetSortMethodMethod.Invoke(instance, [Enum.ToObject(SortMethodType, 1)]);
            }

            void ProcessBatch()
            {
                if (generation != _generation || instance.Dispatcher.HasShutdownStarted) return;

                var end = Math.Min(index + BatchSize, itemsToShow.Count);
                for (; index < end; index++)
                {
                    var entity = itemsToShow[index];
                    var item = (MyLocalCompItem)BuildItemMethod!.Invoke(instance, [entity])!;
                    instance.modItems[entity.RawPath] = item;
                }

                if (index < itemsToShow.Count)
                    instance.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ProcessBatch));
                else
                    instance.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Finish));
            }

            if (instance.Dispatcher.CheckAccess())
                ProcessBatch();
            else
                instance.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ProcessBatch));
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("分批加载 Mod 列表失败：" + ex);
            ModBase.Log(ex, "加载 Mod 列表 UI 失败", ModBase.LogLevel.Feedback);
        }
    }

    private static void ShowEmptyState(PageInstanceCompResource instance)
    {
        var compType = CompTypeField?.GetValue(instance);
        var isSchematic = compType is not null &&
                          Convert.ToInt32(compType) == (int)ModComp.CompType.Schematic;

        if (isSchematic)
        {
            var folder = GetCompFolderMethod?.Invoke(null, [PageInstanceLeft.McInstance, compType]) as string;
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                SetVisibility(PanSchematicEmptyField, instance, Visibility.Visible);
                SetVisibility(PanEmptyField, instance, Visibility.Collapsed);
                SetVisibility(PanBackField, instance, Visibility.Collapsed);
                return;
            }
        }

        var currentFolderPath = instance.CurrentFolderPath;
        if (isSchematic)
        {
            var inFolder = !string.IsNullOrEmpty(currentFolderPath);
            SetText(EmptyTitleField, instance,
                Lang.Text(inFolder ? "Instance.Resource.EmptyFolder.Title" : "Instance.Resource.Empty.Title"));
            SetText(EmptyDescriptionField, instance,
                Lang.Text(inFolder
                    ? "Instance.Resource.EmptyFolder.Description"
                    : "Instance.Resource.Empty.Description"));
        }
        else
        {
            SetText(EmptyTitleField, instance, Lang.Text("Instance.Resource.Empty.Title"));
            SetText(EmptyDescriptionField, instance, Lang.Text("Instance.Resource.Empty.DescriptionWithDownload"));
        }

        SetVisibility(HintBackField, instance,
            string.IsNullOrEmpty(currentFolderPath) ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility(PanEmptyField, instance, Visibility.Visible);
        SetVisibility(PanBackField, instance, Visibility.Collapsed);
        SetVisibility(PanSchematicEmptyField, instance, Visibility.Collapsed);
    }

    private static void SetVisibility(FieldInfo? field, object instance, Visibility visibility)
    {
        if (field?.GetValue(instance) is UIElement element)
            element.Visibility = visibility;
    }

    private static void SetText(FieldInfo? field, object instance, string text)
    {
        if (field?.GetValue(instance) is System.Windows.Controls.TextBlock block)
            block.Text = text;
    }
}
