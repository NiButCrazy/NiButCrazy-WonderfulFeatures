using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using PCL.Core.App.Localization;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 重写 MySkin.Load：
/// - 只读取皮肤文件，绝不写回来源文件；
/// - 按 8x8（随皮肤分辨率缩放）截取脸部和帽子层；
/// - 用皮肤地址哈希作为头像缓存 ID，避免路径解析产生非法文件名。
/// </summary>
[Mixin("PCL.MySkin")]
internal static class AvatarMixin
{
    private static readonly FieldInfo? BackField = typeof(MySkin).GetField(
        "ImgBack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? ForeField = typeof(MySkin).GetField(
        "ImgFore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? ProfileSkinPageField = typeof(PageLoginProfileSkin).GetField(
        "Skin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? TextNameField = typeof(PageLoginProfileSkin).GetField(
        "TextName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? TextTypeField = typeof(PageLoginProfileSkin).GetField(
        "TextType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>
    /// 离线档案刷新头像时只重启离线皮肤加载器，不删除任何皮肤缓存，
    /// 避免档案选择列表里其他档案的头像被清空。
    /// </summary>
    [Inject("RefreshCache", At = MixinAt.Head, Cancellable = true)]
    private static void RefreshCache(CallbackInfo callback)
    {
        var profile = ModProfile.selectedProfile;
        if (profile is null || profile.Type != ModLaunch.McLoginType.Legacy) return;
        callback.Cancel();

        try
        {
            var hasLoaderRunning = PageLaunchLeft.skinLoaders.Any(
                loader => loader.State == ModBase.LoadState.Loading);
            if (hasLoaderRunning)
            {
                HintService.Hint(Lang.Text("Launch.Skin.Refresh.Busy"));
                return;
            }

            ModBase.RunInThread(() =>
            {
                try
                {
                    HintService.Hint(Lang.Text("Launch.Skin.Refreshing"));
                    PageLaunchLeft.skinLegacy.WaitForExit(isForceRestart: true);
                    HintService.Hint(Lang.Text("Launch.Skin.RefreshSuccess"), HintType.Success);
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, Lang.Text("Launch.Skin.Refresh.Error"), ModBase.LogLevel.Msgbox);
                }
            });
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("刷新离线头像失败：" + ex);
        }
    }

    [Overwrite("Load")]
    private static void Load([This] MySkin instance)
    {
        // 启动后 2 秒内（窗口入场动画期间）把皮肤处理延后，避免抢占 UI 线程。
        if (PluginBootstrap.IsStartupWindow)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                _ = instance.Dispatcher.BeginInvoke(new Action(() => LoadCore(instance)));
            });
            return;
        }

        LoadCore(instance);
    }

    private static void LoadCore(MySkin instance)
    {
        var back = BackField?.GetValue(instance) as Image;
        var fore = ForeField?.GetValue(instance) as Image;

        try
        {
            var address = instance.loader?.output ?? string.Empty;
            instance.Address = address;

            if (string.IsNullOrWhiteSpace(address))
                throw new InvalidOperationException("皮肤加载器没有输出");
            if (!address.StartsWith(ModBase.pathImage, StringComparison.Ordinal) && !File.Exists(address))
                throw new FileNotFoundException("皮肤文件未找到", address);

            MyBitmap image;
            try
            {
                image = new MyBitmap(address);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("皮肤图片无法读取", ex);
            }

            if (back is not null) back.Tag = address;

            var scale = image.PixelWidth / 64;
            if (image.PixelWidth < 64 || image.PixelHeight < 32 ||
                image.PixelWidth % 64 != 0 ||
                (image.PixelHeight != image.PixelWidth && image.PixelHeight != image.PixelWidth / 2))
                throw new InvalidDataException(
                    $"皮肤尺寸无效（{image.PixelWidth}×{image.PixelHeight}），请选择 64×64、64×32 或对应的高清 PNG。");

            var face = image.Clip(scale * 8, scale * 8, scale * 8, scale * 8);
            MyBitmap? hat = null;
            if (image.PixelHeight >= 64)
                hat = image.Clip(scale * 40, scale * 8, scale * 8, scale * 8);

            if (fore is not null)
            {
                fore.Source = hat;
                if (hat is not null)
                    FadeIn(fore, 360, 60);
                else
                    fore.Opacity = 1;
            }
            if (back is not null)
            {
                back.Source = face;
                FadeIn(back, 360, 0);
            }

            FadeProfileText(instance);

            // 头像缓存只写入独立目录，来源皮肤文件保持只读。
            var headId = BuildHeadId(address);
            var headDirectory = Path.Combine(ModBase.pathTemp, "Cache", "Skin", "Head");
            Directory.CreateDirectory(headDirectory);
            var cachePath = Path.Combine(headDirectory, headId + ".png");

            // 缓存已存在且不比来源文件旧时直接复用，避免每次启动都在 UI 线程
            // 重建头像、拖慢窗口入场动画。换肤会更新来源文件时间，从而触发重建。
            var needsRebuild = !File.Exists(cachePath);
            if (!needsRebuild && File.Exists(address))
            {
                try
                {
                    needsRebuild = File.GetLastWriteTimeUtc(address) > File.GetLastWriteTimeUtc(cachePath);
                }
                catch
                {
                    needsRebuild = true;
                }
            }

            if (needsRebuild)
            {
                var head = MyBitmap.Create(56, 56)
                    .Overlay(face.Scale(48, 48), 4, 4);
                if (hat is not null)
                    head = head.Overlay(hat.Scale(56, 56), 0, 0);
                head.Save(cachePath);
            }

            if (ModProfile.selectedProfile is not null &&
                ModProfile.selectedProfile.SkinHeadId != headId)
            {
                ModProfile.selectedProfile.SkinHeadId = headId;
                ModProfile.SaveProfile();
            }

            ModBase.Log("[Skin] 载入头像成功：" + (instance.loader?.name ?? "未知"));
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("头像加载失败：" + ex);
            if (fore is not null) fore.Source = null;
            if (back is not null) back.Source = null;
            ModBase.Log(ex, "皮肤加载失败", ModBase.LogLevel.Hint);
        }
    }

    private static void FadeProfileText(MySkin instance)
    {
        try
        {
            var page = ModMain.frmLoginProfileSkin;
            if (page is null) return;
            if (ProfileSkinPageField?.GetValue(page) is not MySkin pageSkin ||
                !ReferenceEquals(pageSkin, instance))
                return;

            if (TextNameField?.GetValue(page) is UIElement name)
                FadeIn(name, 360, 30);
            if (TextTypeField?.GetValue(page) is UIElement type)
                FadeIn(type, 360, 60);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("昵称过渡失败：" + ex);
        }
    }

    private static void FadeIn(UIElement element, int durationMilliseconds, int delayMilliseconds)
    {
        element.Opacity = 0d;
        var animation = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1d;
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static string BuildHeadId(string address)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(address));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>
/// 在档案皮肤页刷新文字之前先把它设为透明，
/// 等 MySkin.Load 完成后与头像一起淡入，避免「不透明 → 透明 → 不透明」。
/// </summary>
[Mixin("PCL.PageLoginProfileSkin")]
internal static class ProfileTextFadeMixin
{
    private static readonly FieldInfo? TextNameField = typeof(PageLoginProfileSkin).GetField(
        "TextName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? TextTypeField = typeof(PageLoginProfileSkin).GetField(
        "TextType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [Inject("Reload", At = MixinAt.Head)]
    private static void HideTextBeforeReload([This] PageLoginProfileSkin instance)
    {
        Hide(TextNameField, instance);
        Hide(TextTypeField, instance);
        UpdateSkinMenuText(instance);
    }

    private static void Hide(FieldInfo? field, object instance)
    {
        if (field?.GetValue(instance) is UIElement element)
            element.Opacity = 0d;
    }

    /// <summary>
    /// 离线档案的「修改皮肤」菜单改为「修改头像」，避免误解为修改游戏内皮肤。
    /// </summary>
    private static void UpdateSkinMenuText(PageLoginProfileSkin page)
    {
        try
        {
            if (page.FindName("BtnSkin") is not MyIconButton button) return;
            if (button.ContextMenu is null || button.ContextMenu.Items.Count == 0) return;
            if (button.ContextMenu.Items[0] is not MyMenuItem item) return;

            var profile = ModProfile.selectedProfile;
            var isLegacy = profile is not null && profile.Type == ModLaunch.McLoginType.Legacy;
            item.Header = isLegacy
                ? "修改头像"
                : Lang.Text("Launch.Account.ProfileSkin.ChangeSkin");

            // 离线档案没有可保存/修改的正版皮肤与披风，隐藏这两项。
            if (button.ContextMenu.Items.Count > 1 && button.ContextMenu.Items[1] is MyMenuItem saveItem)
                saveItem.Visibility = isLegacy ? Visibility.Collapsed : Visibility.Visible;
            if (button.ContextMenu.Items.Count > 3 && button.ContextMenu.Items[3] is MyMenuItem capeItem)
                capeItem.Visibility = isLegacy ? Visibility.Collapsed : Visibility.Visible;

            // 离线档案额外提供删除自定义头像。
            var deleteItem = EnsureDeleteMenuItem(button.ContextMenu);
            deleteItem.Visibility = isLegacy ? Visibility.Visible : Visibility.Collapsed;

            // 离线档案额外提供按玩家 ID 获取正版头像。
            var mojangItem = EnsureMojangMenuItem(button.ContextMenu);
            mojangItem.Visibility = isLegacy ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("更新档案菜单文字失败：" + ex);
        }
    }

    private static MyMenuItem EnsureMojangMenuItem(ContextMenu menu)
    {
        foreach (var item in menu.Items)
            if (item is MyMenuItem existing && existing.Tag as string == "NbcMojangAvatar")
                return existing;

        var mojangItem = new MyMenuItem
        {
            Header = "正版头像",
            Tag = "NbcMojangAvatar"
        };
        mojangItem.Click += (_, _) => ApplyMojangAvatar();
        // 放在「修改头像」下面。
        if (menu.Items.Count > 0)
            menu.Items.Insert(1, mojangItem);
        else
            menu.Items.Add(mojangItem);
        return mojangItem;
    }

    private static void ApplyMojangAvatar()
    {
        try
        {
            var profile = ModProfile.selectedProfile;
            if (profile is null || profile.Type != ModLaunch.McLoginType.Legacy)
                return;

            var id = profile.Username?.Trim() ?? string.Empty;
            if (id.Length < 3)
            {
                HintService.Hint("当前档案的玩家 ID 无效", HintType.Error);
                return;
            }

            HintService.Hint("正在获取正版皮肤...");
            ModBase.RunInNewThread(() =>
            {
                try
                {
                    var uuid = (string)ModProfile.McLoginMojangUuid(id, true);
                    var address = ModSkin.McSkinGetAddress(uuid, "Mojang");
                    address = ModSkin.McSkinDownload(address);
                    ModBase.RunInUi(() => PluginBootstrap.ApplyOfflineSkin(profile, address));
                    ModBase.RunInUi(() => HintService.Hint("已应用正版头像", HintType.Success));
                }
                catch (Exception ex)
                {
                    PluginBootstrap.WriteLog("获取正版头像失败：" + ex);
                    ModBase.RunInUi(() => HintService.Hint(
                        "获取正版皮肤失败，请检查玩家 ID 或网络", HintType.Error));
                }
            }, "Mojang Avatar");
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("获取正版头像失败：" + ex);
        }
    }

    private static MyMenuItem EnsureDeleteMenuItem(ContextMenu menu)
    {
        foreach (var item in menu.Items)
            if (item is MyMenuItem existing && existing.Tag as string == "NbcDeleteAvatar")
                return existing;

        var deleteItem = new MyMenuItem
        {
            Header = "删除头像",
            Tag = "NbcDeleteAvatar"
        };
        deleteItem.Click += (_, _) => DeleteAvatar();
        menu.Items.Add(deleteItem);
        return deleteItem;
    }

    private static void DeleteAvatar()
    {
        try
        {
            var profile = ModProfile.selectedProfile;
            if (profile is null || profile.Type != ModLaunch.McLoginType.Legacy)
                return;

            var path = OfflineSkinMixin.FindCustomSkin(profile.Username, profile.Uuid);
            if (string.IsNullOrWhiteSpace(path))
            {
                HintService.Hint("当前档案没有自定义头像", HintType.Info);
                return;
            }

            if (ModMain.MyMsgBox(
                    $"确定删除头像文件吗？\n{Path.GetFileName(path)}",
                    "删除头像",
                    "删除",
                    "取消") != 1)
                return;

            ModBase.RunInThread(() =>
            {
                try
                {
                    File.Delete(path);
                    PageLaunchLeft.skinLegacy.WaitForExit(isForceRestart: true);
                    ModBase.RunInUi(() => HintService.Hint("头像已删除", HintType.Success));
                }
                catch (Exception ex)
                {
                    PluginBootstrap.WriteLog("删除头像失败：" + ex);
                    ModBase.RunInUi(() => HintService.Hint("删除头像失败", HintType.Error));
                }
            });
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("删除头像失败：" + ex);
        }
    }
}
