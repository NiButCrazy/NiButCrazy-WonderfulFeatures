using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 本地 Mod 重复项检测。
/// 原版的「重复」筛选只按联网匹配到的资源项目 Id 分组，本地 Mod（尤其是未收录或联网失败的文件）
/// 永远不会被识别。这里在保留在线 Id 分组的基础上，优先按 jar 内解析出的 modId 分组，
/// 因此两份不同版本的同一个 Mod 即使没有联网信息也能被发现。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class DuplicateDetectMixin
{
    private static readonly FieldInfo? BtnFilterDuplicateField = typeof(PageInstanceCompResource).GetField(
        "BtnFilterDuplicate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private sealed class DuplicateState
    {
        public bool Initialized;
        public string Signature = string.Empty;
        public HashSet<string> Paths = new(StringComparer.Ordinal);

        public bool VisibleHooked;
        public bool Coercing;
    }

    // 每个页面实例单独缓存：不同资源页面的加载器输出不同，共用会互相串数据。
    private static readonly ConditionalWeakTable<PageInstanceCompResource, DuplicateState> States = new();

    /// <summary>列表内容可能变化时重建缓存，避免每次 CanPassFilter 都遍历全部 Mod。</summary>
    internal static void RefreshCache(PageInstanceCompResource instance)
    {
        try
        {
            Rebuild(instance);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("本地重复 Mod 检测失败：" + ex);
        }
    }

    [Inject("CanPassFilter", At = MixinAt.Head, Cancellable = true)]
    private static void CanPassFilter(
        [This] PageInstanceCompResource instance,
        [Arg(0)] ModLocalComp.LocalCompFile checkingMod,
        CallbackInfo<bool> callback)
    {
        if (instance.Filter != PageInstanceCompResource.FilterType.Duplicate) return;

        var state = GetCached(instance);
        callback.SetReturnValue(state.Paths.Contains(checkingMod.RawPath ?? string.Empty));
    }


    private static DuplicateState GetCached(PageInstanceCompResource instance)
    {
        var state = States.GetOrCreateValue(instance);
        if (!state.Initialized)
            try
            {
                Rebuild(instance);
            }
            catch (Exception ex)
            {
                PluginBootstrap.WriteLog("本地重复 Mod 检测失败：" + ex);
                state.Initialized = true;
            }

        return state;
    }

    private static void Rebuild(PageInstanceCompResource instance)
    {
        var state = States.GetOrCreateValue(instance);

        // 只以当前页面自己的条目为准。共享 loader 的 output 可能停留在其它资源类型上
        // （例如页面复用了缓存且没有重新加载），直接读它会得到另一类资源的数据。
        var entries = new List<ModLocalComp.LocalCompFile>();
        foreach (var item in instance.modItems.Values)
            if (item.Entry is { IsFolder: false } entry)
                entries.Add(entry);

        // 签名包含路径、modId 与在线项目 Id；任一变化（启禁用、联网信息回填）都会触发重算。
        var signature = new StringBuilder();
        foreach (var entry in entries)
            signature.Append(entry.RawPath).Append('\u0001')
                .Append(entry.ModId ?? string.Empty).Append('\u0001')
                .Append(entry.Comp?.Id ?? string.Empty).Append('\u0002');

        var newSignature = signature.ToString();
        if (state.Initialized && state.Signature == newSignature) return;

        state.Initialized = true;
        state.Signature = newSignature;
        state.Paths = FindDuplicatePaths(entries);
    }

    /// <summary>
    /// 按钮计数只统计当前视图里实际存在的项，和点击「重复」后列表里显示的条数保持一致；
    /// 筛选本身仍使用完整集合，子文件夹里的重复项在进入对应文件夹后同样会被标出。
    /// </summary>
    private static int CountVisible(PageInstanceCompResource instance, DuplicateState state)
    {
        if (state.Paths.Count == 0) return 0;

        var count = 0;
        foreach (var key in instance.modItems.Keys)
            if (state.Paths.Contains(key))
                count++;

        return count;
    }

    /// <summary>
    /// 返回所有属于重复组的条目路径（RawPath）。
    /// 并查集：同一 modId 或同一在线项目 Id 的条目连在一起，
    /// 这样即使其中一份解析失败、只剩联网信息，也不会漏掉另一份的重复关系。
    /// </summary>
    internal static HashSet<string> FindDuplicatePaths(IReadOnlyList<ModLocalComp.LocalCompFile> entries)
    {
        var parent = new int[entries.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }

            return value;
        }

        void Union(int left, int right)
        {
            var rootLeft = Find(left);
            var rootRight = Find(right);
            if (rootLeft != rootRight) parent[rootRight] = rootLeft;
        }

        var byModId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byCompId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            var modId = entry.ModId;
            if (!string.IsNullOrWhiteSpace(modId))
            {
                if (byModId.TryGetValue(modId, out var other))
                    Union(i, other);
                else
                    byModId[modId] = i;
            }

            var compId = entry.Comp?.Id;
            if (!string.IsNullOrWhiteSpace(compId))
            {
                if (byCompId.TryGetValue(compId, out var other))
                    Union(i, other);
                else
                    byCompId[compId] = i;
            }
        }

        var groupSizes = new Dictionary<int, int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var root = Find(i);
            groupSizes[root] = groupSizes.GetValueOrDefault(root) + 1;
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
            if (groupSizes[Find(i)] > 1)
                paths.Add(entries[i].RawPath ?? string.Empty);

        return paths;
    }

    /// <summary>
    /// 原版 RefreshBars 的异步统计结束后调用，用本地检测结果覆盖它在「重复」按钮上写的在线结果。
    /// </summary>
    internal static void ApplyAfterRefreshBars(PageInstanceCompResource instance)
    {
        try
        {
            RefreshCache(instance);
            ApplyBar(instance, States.GetOrCreateValue(instance));
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("刷新重复 Mod 计数失败：" + ex);
        }
    }

    private static void ApplyBar(PageInstanceCompResource instance, DuplicateState state)
    {
        if (BtnFilterDuplicateField?.GetValue(instance) is not MyRadioButton button) return;

        // 原版 RefreshBars 的异步统计只认在线项目 Id，会把本地重复项对应的按钮隐藏或改回旧数字。
        // 在 IsVisibleChanged 里同步纠正，避免出现「隐藏→显示」的闪烁。
        if (!state.VisibleHooked)
        {
            state.VisibleHooked = true;
            button.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is false) CoerceButton(instance, state);
            };
        }

        if (ModListLoadMixin.IsLoadingInProgress(instance)) return;

        var count = CountVisible(instance, state);
        var text = Lang.Text("Instance.Resource.Filter.DuplicateWithCount", count);
        if (!string.Equals(button.Text, text, StringComparison.Ordinal)) button.Text = text;

        var visibility = instance.Filter == PageInstanceCompResource.FilterType.Duplicate || count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (button.Visibility != visibility) button.Visibility = visibility;
    }

    private static void CoerceButton(PageInstanceCompResource instance, DuplicateState state)
    {
        if (state.Coercing) return;

        state.Coercing = true;
        try
        {
            ApplyBar(instance, state);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("校正重复 Mod 按钮失败：" + ex);
        }
        finally
        {
            state.Coercing = false;
        }
    }
}
