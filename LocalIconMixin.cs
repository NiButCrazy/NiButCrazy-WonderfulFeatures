using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 补上 Forge / NeoForge 的本地图标解析。
/// 采用后台惰性策略：GetLogo 时如果已有在线图标或本地图标，直接返回；
/// 只有确实没有图标时才在后台读取 mods.toml，完成后刷新列表项。
/// </summary>
[Mixin(typeof(ModLocalComp.LocalCompFile))]
internal static class LocalIconMixin
{
    private static readonly ConditionalWeakTable<ModLocalComp.LocalCompFile, StrongBox<bool>> Checked = new();

    [Overwrite("GetLogo")]
    private static string GetLogo([This] ModLocalComp.LocalCompFile instance)
    {
        if (instance.Comp is not null && instance.Comp.LogoUrl is not null)
            return instance.Comp.LogoUrl;

        if (!string.IsNullOrWhiteSpace(instance.Logo))
            return instance.Logo;

        if (!Checked.TryGetValue(instance, out _))
        {
            Checked.Add(instance, new StrongBox<bool>(true));
            StartBackgroundExtract(instance);
        }

        if (instance.IsFolder)
            return "pack://application:,,,/images/Icons/Folder.png";
        return ModBase.pathImage + "Icons/NoIcon.png";
    }

    private static void StartBackgroundExtract(ModLocalComp.LocalCompFile instance)
    {
        var loader = instance.DetectedLoader;
        if (string.IsNullOrWhiteSpace(loader) ||
            !(loader.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
              loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase)))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var target = ExtractLogoPath(instance);
                if (target is null) return;

                ModBase.RunInUi(() =>
                {
                    if (instance.Comp is not null && instance.Comp.LogoUrl is not null) return;
                    if (!string.IsNullOrWhiteSpace(instance.Logo)) return;

                    instance.Logo = target!;
                    // 触发 MyLocalCompItem.Refresh，让已经显示的列表项换成新图标。
                    instance.Comp = instance.Comp!;
                });
            }
            catch (Exception ex)
            {
                PluginBootstrap.WriteLog("提取 Forge 本地图标失败：" + ex);
            }
        });
    }

    private static string? ExtractLogoPath(ModLocalComp.LocalCompFile instance)
    {
        using var jar = new ZipArchive(
            new FileStream(instance.path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete));

        var tomlEntry = jar.GetEntry("META-INF/neoforge.mods.toml")
                        ?? jar.GetEntry("META-INF/mods.toml");
        if (tomlEntry is null) return null;

        string tomlText;
        using (var reader = new StreamReader(tomlEntry.Open(), Encoding.UTF8))
            tomlText = reader.ReadToEnd();

        var logoFile = FindLogoFile(tomlText);
        if (string.IsNullOrWhiteSpace(logoFile)) return null;

        var logoEntry = jar.GetEntry(logoFile);
        if (logoEntry is null) return null;

        var md5 = ModBase.GetStringMD5(logoEntry.Length + logoEntry.CompressedLength + instance.path);
        var target = Path.Combine(ModBase.pathTemp, "Cache", "Images", md5 + ".png");

        // 图标已缓存时直接复用，避免每次刷新 Mod 列表都重写一遍图片。
        if (File.Exists(target) && new FileInfo(target).Length > 0)
            return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var stream = logoEntry.Open())
            ModBase.WriteFile(target, stream);

        return target;
    }

    /// <summary>
    /// 从 [[mods]] 段读取第一个 logoFile；多 Mod 合一的 jar 取第一个即可。
    /// </summary>
    private static string? FindLogoFile(string tomlText)
    {
        var inModsSection = false;
        foreach (var rawLine in tomlText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                var section = line.Trim('[', ']').Trim();
                inModsSection = section.Equals("mods", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inModsSection) continue;

            var equals = line.IndexOf('=');
            if (equals < 0) continue;

            var key = line[..equals].Trim();
            if (!key.Equals("logoFile", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(equals + 1)..].Trim();
            if (value.Length == 0) continue;

            if (value[0] is '"' or '\'')
            {
                var quote = value[0];
                var end = value.IndexOf(quote, 1);
                if (end > 0) value = value[1..end];
            }
            else
            {
                var comment = value.IndexOf('#');
                if (comment >= 0) value = value[..comment].Trim();
            }

            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}
