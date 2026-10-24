using PCL.Core.UI;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 让离线档案的「修改皮肤」菜单可用：选择本地 PNG 后写入插件皮肤目录，
/// 其余登录类型保持原版行为。
/// </summary>
[Mixin("PCL.PageLoginProfileSkin")]
internal static class ProfileSkinMixin
{
    [Inject("Skin_Click", At = MixinAt.Head, Cancellable = true)]
    private static void SkinClick(CallbackInfo callback)
    {
        var profile = ModProfile.selectedProfile;
        if (profile is null || profile.Type != ModLaunch.McLoginType.Legacy) return;

        callback.Cancel();
        var file = SystemDialogs.SelectFile("皮肤文件|*.png", "选择离线皮肤");
        if (string.IsNullOrWhiteSpace(file)) return;

        PluginBootstrap.ApplyOfflineSkin(profile, file);
    }
}
