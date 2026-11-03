using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 资源管理页的页面级缓存。
/// PCL 的四个资源页共用同一个 compResourceListLoader，而“文件夹是否更新”的检查只记住最后一次的路径，
/// 因此在模组/资源包之间来回切换时总会判定为需要重新加载，即使目录内容完全没有变化。
/// 这里为每个页面记住“上次成功加载的目录指纹”，再次进入且指纹一致时直接复用已有列表，不启动加载器。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class LocalListCacheMixin
{
    private sealed class CacheState
    {
        public bool Valid;
        public string? Fingerprint;

        /// <summary>上次加载得到的完整输出；复用时写回共享 loader，保证操作按钮仍按当前页的条目工作。</summary>
        public List<ModLocalComp.LocalCompFile>? Entries;
    }

    private static readonly ConditionalWeakTable<PageInstanceCompResource, CacheState> States = new();

    private static readonly FieldInfo? CompTypeField = typeof(PageInstanceCompResource).GetField(
        "currentCompType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? GetCompFolderMethod = typeof(PageInstanceCompResource).GetMethod(
        "GetCompFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>
    /// 加载器输出是否属于当前页面。共用 loader 完成时会通知所有页面，
    /// 如果不隔离，资源包加载会把隐藏的模组页列表清空。
    /// </summary>
    internal static bool IsOutputForPage(PageInstanceCompResource instance)
    {
        try
        {
            var input = ModLocalComp.compResourceListLoader.input;
            if (input is null) return true;

            var pageType = CompTypeField?.GetValue(instance);
            return pageType is not null && Convert.ToInt32(pageType) == Convert.ToInt32(input.compType);
        }
        catch
        {
            return true;
        }
    }

    [Inject("ReloadCompFileList", At = MixinAt.Head, Cancellable = true)]
    private static void TryReuse(
        [This] PageInstanceCompResource instance,
        [Arg(0)] bool forceReload,
        CallbackInfo callback)
    {
        // 手动刷新、下载完成、启禁用等路径必须真正重载。
        if (forceReload) return;

        // 加载器仍在运行（例如快速切换页面）时不要复用，避免共享状态错乱。
        if (ModLocalComp.compResourceListLoader.State != ModBase.LoadState.Finished) return;
        if (ModListLoadMixin.IsBatchLoading(instance) || ModListLoadMixin.IsLoadingInProgress(instance)) return;

        var state = States.GetOrCreateValue(instance);
        if (!state.Valid || state.Fingerprint is null) return;

        var fingerprint = ComputeFingerprint(instance);
        if (fingerprint is null || fingerprint != state.Fingerprint) return;

        // 共享 loader 的 output 可能还停留在其它资源类型上。
        // 当前页的启禁用、删除、分享等操作都会读取它，复用时必须切回本页的条目。
        if (state.Entries is not null)
            ModLocalComp.compResourceListLoader.output = state.Entries;

        callback.Cancel();
    }

    /// <summary>
    /// 一次完整的列表 UI 构建结束后调用，记录当前目录指纹。
    /// </summary>
    internal static void MarkLoaded(PageInstanceCompResource instance)
    {
        var state = States.GetOrCreateValue(instance);
        var fingerprint = ComputeFingerprint(instance);
        state.Fingerprint = fingerprint;
        state.Valid = fingerprint is not null;
        if (state.Valid)
            state.Entries = ModLocalComp.compResourceListLoader.output;
    }

    internal static void Invalidate(PageInstanceCompResource instance)
    {
        var state = States.GetOrCreateValue(instance);
        state.Valid = false;
        state.Fingerprint = null;
        state.Entries = null;
    }

    /// <summary>
    /// 只在根目录使用缓存。进入子文件夹后列表内容不同，直接禁用该页缓存。
    /// 目录修改时间能覆盖新增、删除、重命名（启禁用）等操作，文件数量作为额外保险。
    /// </summary>
    private static string? ComputeFingerprint(PageInstanceCompResource instance)
    {
        try
        {
            if (!string.IsNullOrEmpty(instance.CurrentFolderPath)) return null;

            var compType = CompTypeField?.GetValue(instance);
            if (compType is null) return null;

            var folder = GetCompFolderMethod?.Invoke(null, [PageInstanceLeft.McInstance, compType]) as string;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

            var info = new DirectoryInfo(folder);
            var entryCount = Directory.GetFileSystemEntries(folder).Length;
            return $"{Convert.ToInt32(compType)}|{Path.TrimEndingDirectorySeparator(folder)}|" +
                   $"{info.LastWriteTimeUtc.Ticks}|{entryCount}";
        }
        catch
        {
            return null;
        }
    }
}
