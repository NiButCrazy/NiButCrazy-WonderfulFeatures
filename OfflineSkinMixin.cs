using System;
using System.IO;
using System.Reflection;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 替换离线皮肤加载逻辑：优先使用插件 Skin 目录里的本地皮肤，
/// 否则沿用 PCL 原版的 Steve/Alex 选择。
/// </summary>
[Mixin("PCL.PageLaunchLeft")]
internal static class OfflineSkinMixin
{
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

    internal static string ResolveSkinAddress(string username, string uuid)
    {
        var customSkin = FindCustomSkin(username, uuid);
        if (customSkin is not null) return customSkin;

        return ModBase.pathImage + "Skins/" + ModSkin.McSkinSex(uuid) + ".png";
    }

    private static string? FindCustomSkin(string username, string uuid)
    {
        var directory = PluginSettings.SkinDirectory;
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
}
