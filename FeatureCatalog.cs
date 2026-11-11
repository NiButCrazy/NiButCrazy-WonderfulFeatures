using System;
using System.Collections.Generic;

namespace PCL.Plugin.NiButCrazy;

/// <summary>
/// 插件功能分组。设置页按分组开关，Mixin 配置处理器据此决定是否应用。
/// 未列出的 Mixin（工具页设置）始终启用。
/// </summary>
internal static class FeatureCatalog
{
    public sealed record Feature(string Key, string Name, string[] Mixins);

    public static readonly Feature[] Features =
    [
        new("avatar", "离线皮肤与头像", [
            "PCL.Plugin.NiButCrazy.Mixins.OfflineSkinMixin",
            "PCL.Plugin.NiButCrazy.Mixins.ProfileSkinMixin",
            "PCL.Plugin.NiButCrazy.Mixins.AvatarMixin",
            "PCL.Plugin.NiButCrazy.Mixins.ProfileTextFadeMixin"
        ]),
        new("performance", "资源列表加载与切换优化", [
            "PCL.Plugin.NiButCrazy.Mixins.ModListLoadMixin",
            "PCL.Plugin.NiButCrazy.Mixins.RefreshBarsMixin",
            "PCL.Plugin.NiButCrazy.Mixins.ModListRefreshMixin",
            "PCL.Plugin.NiButCrazy.Mixins.LocalListCacheMixin"
        ]),
        new("localmod", "本地 Mod 图标与重复检测", [
            "PCL.Plugin.NiButCrazy.Mixins.LocalIconMixin",
            "PCL.Plugin.NiButCrazy.Mixins.DuplicateDetectMixin"
        ]),
        new("search", "本地搜索增强（中文 / 拼音）", [
            "PCL.Plugin.NiButCrazy.Mixins.ChineseSearchMixin",
            "PCL.Plugin.NiButCrazy.Mixins.PinyinSearchMixin",
            "PCL.Plugin.NiButCrazy.Mixins.SearchCancellationFixMixin"
        ]),
        new("download", "下载搜索词继承", [
            "PCL.Plugin.NiButCrazy.Mixins.DownloadSearchMixin",
            "PCL.Plugin.NiButCrazy.Mixins.DownloadPageMixin"
        ]),
        new("sort", "资源排序记忆", [
            "PCL.Plugin.NiButCrazy.Mixins.SortMemoryMixin"
        ]),
        new("screenshot", "截图列表缓存", [
            "PCL.Plugin.NiButCrazy.Mixins.ScreenshotCacheMixin"
        ])
    ];

    private static readonly Dictionary<string, string> MixinToFeature = BuildMap();

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var feature in Features)
        foreach (var mixin in feature.Mixins)
            map[mixin] = feature.Key;
        return map;
    }

    public static string? GetFeatureKey(string mixinTypeName)
        => MixinToFeature.TryGetValue(mixinTypeName, out var key) ? key : null;
}
