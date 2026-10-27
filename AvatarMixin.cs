using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
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
                    FadeIn(fore, 180, 30);
                else
                    fore.Opacity = 1;
            }
            if (back is not null)
            {
                back.Source = face;
                FadeIn(back, 180, 0);
            }

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
