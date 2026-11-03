using System;
using System.IO;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 修正「加入时间」排序对文件夹调用 FileInfo 导致的异常日志。
/// 原版 GetModFileInfo 用 FileInfo 读取创建时间/大小，传入目录会抛 FileNotFoundException，
/// 投影页有分类文件夹时会反复记录“获取模组信息失败”。
/// 这里对目录直接返回创建时间和 0 大小，文件仍走上游带缓存的实现。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class SortFileInfoMixin
{
    [Inject("GetModFileInfo", At = MixinAt.Head, Cancellable = true)]
    private static void GetModFileInfo(
        [Arg(0)] string path,
        CallbackInfo<(DateTime CreationTime, long Length)> callback)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            callback.SetReturnValue((Directory.GetCreationTime(path), 0L));
        }
        catch
        {
            // 目录在检查后消失时退回原实现，让上游按原有逻辑记录错误。
        }
    }
}
