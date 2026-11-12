using System;
using System.Reflection;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 修复百宝箱「下载正版玩家皮肤」在取消保存时抛异常的问题：
/// 用户取消保存对话框后直接结束，不再用空路径调用 CopyFile。
/// </summary>
[Mixin("PCL.PageToolsTest", Optional = true)]
internal static class ToolsSkinDownloadFixMixin
{
    private static readonly FieldInfo? SkinIdField = typeof(PageToolsTest).GetField(
        "TextSkinID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Overwrite("BtnSkinSave_Click")]
    private static void BtnSkinSave_Click(
        [This] PageToolsTest page,
        [Arg(0)] object sender,
        [Arg(1)] MouseButtonEventArgs e)
    {
        var id = (SkinIdField?.GetValue(page) as MyTextBox)?.Text ?? string.Empty;
        HintService.Hint(Lang.Text("Tools.Test.Skin.Fetching"));
        ModBase.RunInNewThread(() =>
        {
            try
            {
                if (id.Length < 3)
                {
                    HintService.Hint(Lang.Text("Tools.Test.Skin.InvalidId"));
                }
                else
                {
                    var result = (string)ModProfile.McLoginMojangUuid(id, true);
                    result = ModSkin.McSkinGetAddress(result, "Mojang");
                    result = ModSkin.McSkinDownload(result);
                    ModBase.RunInUi(() =>
                    {
                        var path = SystemDialogs.SelectSaveFile(
                            Lang.Text("Tools.Test.Skin.Save"),
                            $"{id}.png",
                            Lang.Text("Tools.Test.Skin.FileFilter"));
                        if (string.IsNullOrWhiteSpace(path)) return;
                        ModBase.CopyFile(result, path);
                        HintService.Hint(Lang.Text("Tools.Test.Skin.Saved", id), HintType.Success);
                    });
                }
            }
            catch (Exception ex)
            {
                if (ex.ToString().Contains("429"))
                {
                    HintService.Hint(Lang.Text("Tools.Test.Skin.TooFrequent"), HintType.Error);
                    ModBase.Log($"获取正版皮肤失败（{id}）：获取皮肤太过频繁，请 5 分钟后再试！");
                }
                else
                {
                    ModBase.Log(ex, $"获取正版皮肤失败（{id}）");
                }
            }
        });
    }
}
