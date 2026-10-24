using System;
using System.IO;
using System.Reflection;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 替换离线皮肤加载逻辑：优先使用插件 Skin 目录里的本地皮肤，
/// 否则从 9 个官方默认皮肤中按 UUID 稳定选择，而不是只有 Steve/Alex。
/// </summary>
[Mixin("PCL.PageLaunchLeft")]
internal static class OfflineSkinMixin
{
    private static readonly string[] DefaultSkins =
        ["Steve", "Alex", "Ari", "Efe", "Kai", "Makena", "Noor", "Sunny", "Zuri"];

    private static readonly FieldInfo? ProfileSkinField = typeof(PageLoginProfileSkin).GetField(
        "Skin",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Overwrite("SkinLegacyLoad")]
    private static void SkinLegacyLoad(ModLoader.LoaderTask<ModBase.EqualableList<string>, string> data)
    {
        try
        {
            var input = data.input;
            var username = input is { Count: > 0 } ? input[0] ?? string.Empty : string.Empty;
            var uuid = input is { Count: > 1 } ? input[1] ?? string.Empty : string.Empty;

            ModBase.RunInUi(() => GetProfileSkin()?.Clear());

            var address = ResolveSkinAddress(username, uuid);
            PluginBootstrap.WriteLog($"离线皮肤：{username} ({uuid}) -> {address}");
            data.output = address;

            var profileSkin = GetProfileSkin();
            if (profileSkin is not null && ReferenceEquals(profileSkin.loader, data))
                ModBase.RunInUi(profileSkin.Load);
            else if (!data.IsAborted)
                data.input = null!;
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("离线皮肤加载失败：" + ex);
            data.output = ModBase.pathImage + "Skins/Steve.png";
        }
    }

    private static MySkin? GetProfileSkin()
    {
        var form = ModMain.frmLoginProfileSkin;
        return form is null ? null : ProfileSkinField?.GetValue(form) as MySkin;
    }

    private static string ResolveSkinAddress(string username, string uuid)
    {
        var customSkin = FindCustomSkin(username, uuid);
        if (customSkin is not null) return customSkin;

        var skinName = PluginSettings.Current.VariedDefaultSkins
            ? PickDefaultSkin(uuid)
            : ModSkin.McSkinSex(uuid);
        return ModBase.pathImage + "Skins/" + skinName + ".png";
    }

    private static string? FindCustomSkin(string username, string uuid)
    {
        var directory = PluginSettings.SkinsDirectory;
        foreach (var candidate in new[] { username, uuid, "default" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

            var path = Path.Combine(directory, candidate + ".png");
            try
            {
                if (File.Exists(path)) return path;
            }
            catch
            {
                // 忽略无效路径，继续尝试下一个候选。
            }
        }

        return null;
    }

    private static string PickDefaultSkin(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return "Steve";

        // FNV-1a，保证同一个 UUID 每次启动都得到同一个皮肤。
        var hash = 2166136261u;
        foreach (var character in uuid.ToLowerInvariant())
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return DefaultSkins[hash % (uint)DefaultSkins.Length];
    }
}
