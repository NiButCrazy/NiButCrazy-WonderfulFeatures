using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.UI.Theme;
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
                    // 直接调用插件的搜索实现，避免依赖反射到被 Mixin 覆盖的方法。
                    var result = await Task.Run(() => PinyinSearchMixin.Search(instance, searchText), token);
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
    private const int BatchSize = 6;

    private static readonly FieldInfo? PanBackField = FindField("PanBack");
    private static readonly FieldInfo? PanEmptyField = FindField("PanEmpty");
    private static readonly FieldInfo? PanSchematicEmptyField = FindField("PanSchematicEmpty");
    private static readonly FieldInfo? EmptyTitleField = FindField("TxtEmptyTitle");
    private static readonly FieldInfo? EmptyDescriptionField = FindField("TxtEmptyDescription");
    private static readonly FieldInfo? HintBackField = FindField("BtnHintBack");
    private static readonly FieldInfo? SearchBoxField = FindField("SearchBox");
    private static readonly FieldInfo? CompTypeField = FindField("currentCompType");
    private static readonly FieldInfo? CurrentSortField = FindField("currentSortMethod");
    private static readonly FieldInfo? LoadingField = FindField("Load");
    private static readonly FieldInfo? FilterAllField = FindField("BtnFilterAll");
    private static readonly FieldInfo? SortButtonField = FindField("BtnSort");

    private static readonly MethodInfo? BuildItemMethod = typeof(PageInstanceCompResource).GetMethod(
        "BuildLocalCompItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? GetCompFolderMethod = typeof(PageInstanceCompResource).GetMethod(
        "GetCompFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? SetSortMethodMethod = typeof(PageInstanceCompResource).GetMethod(
        "SetSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? GetSortMethodMethod = typeof(PageInstanceCompResource).GetMethod(
        "GetSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly Type? SortMethodType = typeof(PageInstanceCompResource).GetNestedType(
        "SortMethod", BindingFlags.Public | BindingFlags.NonPublic);

    // 每个页面实例单独计数：不同资源页面（Mod/资源包/光影/存档）会并行加载，
    // 共用 static 计数器会互相取消批次，导致列表永远不更新。
    private static readonly ConditionalWeakTable<PageInstanceCompResource, StrongBox<int>> Generations = new();

    private static int NextGeneration(PageInstanceCompResource instance)
    {
        var box = Generations.GetOrCreateValue(instance);
        return Interlocked.Increment(ref box.Value);
    }

    private static bool IsCurrentGeneration(PageInstanceCompResource instance, int generation)
        => Generations.TryGetValue(instance, out var box) && Volatile.Read(ref box.Value) == generation;

    // 批次进行期间禁止其他路径刷新列表，否则会读到尚未提交的空 modItems，
    // 表现为首次加载时先显示 0 个模组。
    private static readonly ConditionalWeakTable<PageInstanceCompResource, StrongBox<bool>> BatchLoading = new();

    internal static bool IsBatchLoading(PageInstanceCompResource instance)
        => BatchLoading.TryGetValue(instance, out var box) && Volatile.Read(ref box.Value);

    private static void SetBatchLoading(PageInstanceCompResource instance, bool value)
    {
        var box = BatchLoading.GetOrCreateValue(instance);
        Volatile.Write(ref box.Value, value);
    }

    // 从开始加载到列表全部就绪之间保持为 true，用于抑制计数刷新。
    private static readonly ConditionalWeakTable<PageInstanceCompResource, StrongBox<bool>> LoadingInProgress = new();

    internal static bool IsLoadingInProgress(PageInstanceCompResource instance)
        => LoadingInProgress.TryGetValue(instance, out var box) && Volatile.Read(ref box.Value);

    private static void SetLoadingInProgress(PageInstanceCompResource instance, bool value)
    {
        var box = LoadingInProgress.GetOrCreateValue(instance);
        Volatile.Write(ref box.Value, value);
    }

    private static void ShowSortingTexts(PageInstanceCompResource instance)
    {
        SetControlText(FilterAllField, instance, "统计中");
        SetControlText(SortButtonField, instance, Lang.Text("Instance.Resource.Sort.Text", "排序中"));
    }

    private static void SetControlText(FieldInfo? field, object instance, string text)
    {
        var control = field?.GetValue(instance);
        control?.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(control, text);
    }

    private static FieldInfo? FindField(string name) => typeof(PageInstanceCompResource).GetField(
        name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Overwrite("LoadUIFromLoaderOutput")]
    private static void LoadUIFromLoaderOutput([This] PageInstanceCompResource instance)
    {
        // 四个资源页共用同一个 loader，完成事件会通知所有页面；
        // 类型不匹配时直接跳过，避免资源包加载把隐藏的模组页列表清空。
        if (!LocalListCacheMixin.IsOutputForPage(instance)) return;

        try
        {
            // loader 在列表控件创建完成之前就会报告 Finished，
            // 手动保持加载动画，避免 UI 还在忙时加载条先消失。
            SetLoadingState(instance, MyLoading.MyLoadingState.Run);
            SetBatchLoading(instance, true);
            SetLoadingInProgress(instance, true);

            var output = ModLocalComp.compResourceListLoader.output;
            if (output.Count == 0)
            {
                SetBatchLoading(instance, false);
                SetLoadingInProgress(instance, false);
                ShowEmptyState(instance);
                LocalListCacheMixin.MarkLoaded(instance);
                SetLoadingState(instance, MyLoading.MyLoadingState.Stop);
                return;
            }

            SetVisibility(PanBackField, instance, Visibility.Visible);
            SetVisibility(PanEmptyField, instance, Visibility.Collapsed);
            SetVisibility(PanSchematicEmptyField, instance, Visibility.Collapsed);

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

            ShowSortingTexts(instance);

            var generation = NextGeneration(instance);

            // 先按保存的排序排列，确保首批就是最终排序里最靠前的 24 个。
            var savedSort = PluginSettings.Current.CompSortMethod;
            if (CurrentSortField is not null)
            {
                var sortValue = Enum.IsDefined(CurrentSortField.FieldType, savedSort)
                    ? Enum.ToObject(CurrentSortField.FieldType, savedSort)
                    : Enum.ToObject(CurrentSortField.FieldType, 1);
                CurrentSortField.SetValue(instance, sortValue);

                if (GetSortMethodMethod is not null && itemsToShow.Count > 1)
                {
                    var sortMethod = (Func<ModLocalComp.LocalCompFile, ModLocalComp.LocalCompFile, int>)
                        GetSortMethodMethod.Invoke(instance, [sortValue])!;
                    itemsToShow.Sort((left, right) => sortMethod(left, right));
                }
            }

            // 先同步创建首批并立即放入列表，确保 PageLoaderState 切换时
            // 列表已经有内容，不会出现空白页面；剩余项继续分批创建。
            var created = new Dictionary<string, MyLocalCompItem>(StringComparer.Ordinal);
            var initialCount = Math.Min(13, itemsToShow.Count);
            for (var i = 0; i < initialCount; i++)
            {
                var entity = itemsToShow[i];
                created[entity.RawPath] = (MyLocalCompItem)BuildItemMethod!.Invoke(instance, [entity])!;
            }

            instance.modItems.Clear();
            foreach (var pair in created)
                instance.modItems[pair.Key] = pair.Value;
            SetBatchLoading(instance, false);
            instance.RefreshUI();
            SetBatchLoading(instance, true);
            ShowSortingTexts(instance);

            var index = initialCount;

            void Finish()
            {
                if (!IsCurrentGeneration(instance, generation) || instance.Dispatcher.HasShutdownStarted) return;

                SetBatchLoading(instance, false);
                SetLoadingInProgress(instance, false);

                instance.modItems.Clear();
                foreach (var pair in created)
                    instance.modItems[pair.Key] = pair.Value;

                instance.Filter = PageInstanceCompResource.FilterType.All;
                if (SearchBoxField?.GetValue(instance) is MySearchBox searchBox)
                    searchBox.Text = string.Empty;
                instance.RefreshUI();

                // 直接使用保存的排序方式。原版固定切到资源名，会让排序记忆再恢复一次，
                // 白白多启动一轮整表 RefreshUI，正好卡在加载动画结束的时刻。
                if (SortMethodType is not null && SetSortMethodMethod is not null)
                {
                    var finalSort = Enum.IsDefined(SortMethodType, savedSort)
                        ? Enum.ToObject(SortMethodType, savedSort)
                        : Enum.ToObject(SortMethodType, 1);
                    SetSortMethodMethod.Invoke(instance, [finalSort]);
                }

                LocalListCacheMixin.MarkLoaded(instance);
                SetLoadingState(instance, MyLoading.MyLoadingState.Stop);
            }

            void ProcessBatch()
            {
                if (!IsCurrentGeneration(instance, generation) || instance.Dispatcher.HasShutdownStarted) return;

                var end = Math.Min(index + BatchSize, itemsToShow.Count);
                for (; index < end; index++)
                {
                    var entity = itemsToShow[index];
                    var item = (MyLocalCompItem)BuildItemMethod!.Invoke(instance, [entity])!;
                    created[entity.RawPath] = item;
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
            LocalListCacheMixin.Invalidate(instance);
            SetBatchLoading(instance, false);
            SetLoadingInProgress(instance, false);
            SetLoadingState(instance, MyLoading.MyLoadingState.Stop);
            PluginBootstrap.WriteLog("分批加载 Mod 列表失败：" + ex);
            ModBase.Log(ex, "加载 Mod 列表 UI 失败", ModBase.LogLevel.Feedback);
        }
    }

    private static void SetLoadingState(PageInstanceCompResource instance, MyLoading.MyLoadingState state)
    {
        if (LoadingField?.GetValue(instance) is MyLoading loading)
            loading.State.LoadingState = state;
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

/// <summary>
/// RefreshBars 的两处配合：批次加载期间抑制计数刷新；以及等原版异步统计结束后
/// 用本地检测结果校正「重复」按钮（避免先被在线结果覆盖、再延迟纠正造成闪烁）。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class RefreshBarsMixin
{
    [Inject("RefreshBars", At = MixinAt.Head, Cancellable = true)]
    private static void SuppressRefreshBars([This] PageInstanceCompResource instance, CallbackInfo callback)
    {
        if (ModListLoadMixin.IsBatchLoading(instance) || ModListLoadMixin.IsLoadingInProgress(instance))
            callback.Cancel();
    }

    /// <summary>
    /// 原版把统计逻辑放进 Dispatcher 上的一段 async lambda，方法本身立即返回，
    /// 拿不到它的完成时刻。这里把传给 BeginInvoke 的委托包一层，
    /// 等整个异步统计结束后再统一刷新一次「重复」按钮。
    /// </summary>
    [ModifyArg("RefreshBars",
        Target = "System.Windows.Threading.Dispatcher::BeginInvoke(System.Delegate, System.Object[])",
        Index = 0)]
    private static Delegate WrapRefreshBarsDelegate(Delegate method)
    {
        var page = FindPage(method);
        if (page is null) return method;

        return new Func<Task>(async () =>
        {
            try
            {
                if (method is Func<Task> factory)
                    await factory();
                else
                    method.DynamicInvoke();
            }
            finally
            {
                DuplicateDetectMixin.ApplyAfterRefreshBars(page);
            }
        });
    }

    /// <summary>
    /// async lambda 通常直接以页面实例作为委托 Target；如果编译器生成了闭包类，
    /// 则在闭包里找一下它持有的页面引用。
    /// </summary>
    private static PageInstanceCompResource? FindPage(Delegate method)
    {
        if (method.Target is PageInstanceCompResource page) return page;
        if (method.Target is not { } target) return null;

        foreach (var field in target.GetType()
                     .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.FieldType == typeof(PageInstanceCompResource) &&
                field.GetValue(target) is PageInstanceCompResource owner)
                return owner;

        return null;
    }
}

/// <summary>
/// 增量刷新 Mod 列表：切换筛选 Tab 时不再清空并重建全部控件，
/// 只移除/插入差异项，并跳过文本未变化的富文本格式化。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class ModListRefreshMixin
{
    private static readonly FieldInfo? PanListField = typeof(PageInstanceCompResource).GetField(
        "PanList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? SearchResultField = typeof(PageInstanceCompResource).GetField(
        "searchResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? SortField = typeof(PageInstanceCompResource).GetField(
        "currentSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? CanPassFilterMethod = typeof(PageInstanceCompResource).GetMethod(
        "CanPassFilter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? GetSortMethodMethod = typeof(PageInstanceCompResource).GetMethod(
        "GetSortMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? LabTitleField = typeof(MyLocalCompItem).GetField(
        "LabTitle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? LabInfoField = typeof(MyLocalCompItem).GetField(
        "LabInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? SetColorfulTextLabMethod =
        typeof(PageInstanceCompResource).Assembly
            .GetType("PCL.ModStyle+MinecraftFormatter")
            ?.GetMethod("SetColorfulTextLab", BindingFlags.Public | BindingFlags.Static,
                null, [typeof(string), typeof(TextBlock), typeof(bool)], null);

    private static readonly ConditionalWeakTable<MyLocalCompItem, StrongBox<string>> FormatSnapshots = new();

    private static readonly ConditionalWeakTable<PageInstanceCompResource, StrongBox<int>> RefreshGenerations = new();

    private static int NextRefreshGeneration(PageInstanceCompResource instance)
    {
        var box = RefreshGenerations.GetOrCreateValue(instance);
        return Interlocked.Increment(ref box.Value);
    }

    private static bool IsCurrentRefreshGeneration(PageInstanceCompResource instance, int generation)
        => RefreshGenerations.TryGetValue(instance, out var box) && Volatile.Read(ref box.Value) == generation;

    /// <summary>
    /// 判断当前控件顺序是否已经是目标顺序的子序列。
    /// 筛选切换（全部/已启用/已禁用/可更新）满足这个条件，可以避免逐个移动控件。
    /// </summary>
    private static bool IsSubsequence(
        IReadOnlyList<MyLocalCompItem> existing,
        IReadOnlyList<MyLocalCompItem> desired)
    {
        var desiredIndex = 0;
        foreach (var item in existing)
        {
            while (desiredIndex < desired.Count && !ReferenceEquals(desired[desiredIndex], item))
                desiredIndex++;
            if (desiredIndex == desired.Count) return false;
            desiredIndex++;
        }

        return true;
    }

    [Overwrite("RefreshUI")]
    private static void RefreshUI([This] PageInstanceCompResource instance)
    {
        // 批次加载期间不刷新列表，避免读到尚未提交的 modItems。
        if (ModListLoadMixin.IsBatchLoading(instance)) return;

        // 列表内容可能已变化（启禁用、联网信息回填），刷新本地重复项缓存，
        // 后续 CanPassFilter 的「重复」筛选直接查表。
        DuplicateDetectMixin.RefreshCache(instance);

        if (PanListField?.GetValue(instance) is not Panel panList) return;

        try
        {
            var generation = NextRefreshGeneration(instance);

            var baseEntries = instance.IsSearching
                ? (IEnumerable<ModLocalComp.LocalCompFile>)(SearchResultField?.GetValue(instance)
                    as List<ModLocalComp.LocalCompFile> ?? [])
                : instance.modItems.Values.Select(item => item.Entry);
            var canPass = (Func<ModLocalComp.LocalCompFile, bool>)Delegate.CreateDelegate(
                typeof(Func<ModLocalComp.LocalCompFile, bool>), instance, CanPassFilterMethod!);
            var showingMods = baseEntries.Where(canPass).ToList();

            if (showingMods.Count > 0)
            {
                var sortMethod = (Func<ModLocalComp.LocalCompFile, ModLocalComp.LocalCompFile, int>)
                    GetSortMethodMethod!.Invoke(instance, [SortField!.GetValue(instance)])!;
                showingMods.Sort((left, right) => sortMethod(left, right));
            }

            var desired = new List<(ModLocalComp.LocalCompFile Entry, MyLocalCompItem Item)>();
            foreach (var entry in showingMods)
                if (instance.modItems.TryGetValue(entry.RawPath, out var item))
                    desired.Add((entry, item));

            void Finish()
            {
                if (!IsCurrentRefreshGeneration(instance, generation)) return;

                var showingPaths = new HashSet<string>(
                    showingMods.Select(entry => entry.RawPath ?? string.Empty), StringComparer.Ordinal);
                instance.selectedMods.RemoveWhere(path => !showingPaths.Contains(path ?? string.Empty));
                instance.RefreshBars();
            }

            if (desired.Count == 0)
            {
                panList.Visibility = Visibility.Collapsed;
                Finish();
                return;
            }

            panList.Visibility = Visibility.Visible;

            // 先移除不再显示的项；插入与首次格式化放到后续帧，避免几百项一次性阻塞。
            var desiredItemList = desired.Select(pair => pair.Item).ToList();
            var desiredItems = new HashSet<MyLocalCompItem>(desiredItemList);
            for (var i = panList.Children.Count - 1; i >= 0; i--)
                if (panList.Children[i] is MyLocalCompItem item && !desiredItems.Contains(item))
                    panList.Children.RemoveAt(i);

            // 筛选切换通常只是子集/超集，原有条目的相对顺序不变。
            // 这种情况只做删除/插入，不逐个 IndexOf 搬运控件。
            var existingItems = panList.Children.OfType<MyLocalCompItem>().ToList();
            var fastOrder = IsSubsequence(existingItems, desiredItemList);

            void ApplyItem(ModLocalComp.LocalCompFile entry, MyLocalCompItem item, bool isDark)
            {
                item.Checked = instance.selectedMods.Contains(entry.RawPath);

                var titleBlock = LabTitleField?.GetValue(item) as TextBlock;
                var infoBlock = LabInfoField?.GetValue(item) as TextBlock;
                if (titleBlock is null || infoBlock is null) return;

                var snapshot = (isDark ? "D" : "L") + "\n" + titleBlock.Text + "\n" + infoBlock.Text;
                var box = FormatSnapshots.GetOrCreateValue(item);
                if (box.Value == snapshot) return;

                SetColorfulTextLabMethod?.Invoke(null, [titleBlock.Text, titleBlock, isDark]);
                SetColorfulTextLabMethod?.Invoke(null, [infoBlock.Text, infoBlock, isDark]);
                box.Value = snapshot;
            }

            void ScheduleNext(Action next)
            {
                if (instance.Dispatcher.HasShutdownStarted) return;
                instance.Dispatcher.BeginInvoke(DispatcherPriority.Background, next);
            }

            const int batchSize = 16;

            if (fastOrder)
            {
                var desiredIndex = 0;
                var existingIndex = 0;
                var insertAt = 0;

                void ProcessFastBatch()
                {
                    if (!IsCurrentRefreshGeneration(instance, generation) ||
                        instance.Dispatcher.HasShutdownStarted)
                        return;

                    ModAnimation.AniBeginControl();
                    try
                    {
                        var end = Math.Min(desiredIndex + batchSize, desired.Count);
                        var isDark = ThemeService.IsDarkMode;
                        for (; desiredIndex < end; desiredIndex++)
                        {
                            var (entry, item) = desired[desiredIndex];
                            if (existingIndex < existingItems.Count &&
                                ReferenceEquals(existingItems[existingIndex], item))
                            {
                                existingIndex++;
                            }
                            else
                            {
                                if (item.Parent is Panel parent && !ReferenceEquals(parent, panList))
                                    parent.Children.Remove(item);
                                panList.Children.Insert(insertAt, item);
                            }

                            insertAt++;
                            ApplyItem(entry, item, isDark);
                        }
                    }
                    finally
                    {
                        ModAnimation.AniEndControl();
                    }

                    if (desiredIndex < desired.Count)
                        ScheduleNext(ProcessFastBatch);
                    else
                        Finish();
                }

                if (instance.Dispatcher.CheckAccess())
                    ProcessFastBatch();
                else
                    ScheduleNext(ProcessFastBatch);
                return;
            }

            var index = 0;

            void ProcessMoveBatch()
            {
                if (!IsCurrentRefreshGeneration(instance, generation) ||
                    instance.Dispatcher.HasShutdownStarted)
                    return;

                ModAnimation.AniBeginControl();
                try
                {
                    var end = Math.Min(index + batchSize, desired.Count);
                    var isDark = ThemeService.IsDarkMode;
                    for (; index < end; index++)
                    {
                        var (entry, item) = desired[index];
                        if (item.Parent is Panel parent && !ReferenceEquals(parent, panList))
                            parent.Children.Remove(item);

                        var currentIndex = panList.Children.IndexOf(item);
                        if (currentIndex != index)
                        {
                            if (currentIndex >= 0) panList.Children.RemoveAt(currentIndex);
                            panList.Children.Insert(index, item);
                        }

                        ApplyItem(entry, item, isDark);
                    }
                }
                finally
                {
                    ModAnimation.AniEndControl();
                }

                if (index < desired.Count)
                    ScheduleNext(ProcessMoveBatch);
                else
                    Finish();
            }

            if (instance.Dispatcher.CheckAccess())
                ProcessMoveBatch();
            else
                ScheduleNext(ProcessMoveBatch);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("增量刷新 Mod 列表失败：" + ex);
            ModBase.Log(ex, "刷新 Mod 列表 UI 失败", ModBase.LogLevel.Feedback);
        }
    }
}
