using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 在「设置 → 插件」分类下、「插件管理」之后插入「奇妙特性设置」入口，
/// 并返回插件自己的设置页面。
/// </summary>
[Mixin("PCL.PageSetupLeft", Optional = true)]
internal static class SetupPluginSettingsMixin
{
    private static readonly FormMain.PageSubType PluginSettingsPageId = (FormMain.PageSubType)103;

    private static ToolsPluginSettingsPage? _page;

    [Inject("PageSetupLeft_Loaded", At = MixinAt.Head)]
    private static void BeforeLoaded([This] PageSetupLeft page) => EnsureItem(page);

    [Inject("PageGet", At = MixinAt.Head, Cancellable = true)]
    private static void PageGet(
        [This] PageSetupLeft page,
        [Arg(0)] FormMain.PageSubType? id,
        CallbackInfo<object> callback)
    {
        try
        {
            var target = id ?? page.pageID;
            if (target != PluginSettingsPageId) return;
            callback.SetReturnValue(_page ??= new ToolsPluginSettingsPage());
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("加载插件设置页面失败：" + ex);
        }
    }

    private static void EnsureItem(PageSetupLeft page)
    {
        try
        {
            if (page.FindName("PanItem") is not StackPanel panItem) return;
            if (panItem.Children.OfType<MyListItem>()
                .Any(item => item.Tag is int tag && tag == (int)PluginSettingsPageId))
                return;

            var item = new MyListItem
            {
                Title = "奇妙特性",
                Type = MyListItem.CheckType.RadioBox,
                Tag = (int)PluginSettingsPageId,
                Height = 36,
                MinPaddingRight = 35,
                VerticalAlignment = VerticalAlignment.Top,
                IsScaleAnimationEnabled = false,
                SvgIcon = "lucide/sparkles"
            };

            item.Check += (_, _) =>
            {
                try
                {
                    page.PageChange(PluginSettingsPageId);
                }
                catch (Exception ex)
                {
                    PluginBootstrap.WriteLog("切换到插件设置页面失败：" + ex);
                }
            };

            if (page.FindName("ItemPluginInstalled") is MyListItem anchor)
            {
                // RadioBox 的互斥由 MyListItem 自己处理，不要手动 SetChecked(false)，
                // 否则会触发额外的互斥并把刚点击的项反选。
                anchor.IsVisibleChanged += (_, _) => item.Visibility = anchor.Visibility;
                item.Visibility = anchor.Visibility;

                var index = panItem.Children.IndexOf(anchor);
                panItem.Children.Insert(index + 1, item);
            }
            else
            {
                panItem.Children.Add(item);
            }
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("插入插件设置入口失败：" + ex);
        }
    }
}

/// <summary>
/// 「奇妙特性」设置页面。内容由插件代码构建，不依赖宿主 XAML。
/// </summary>
internal sealed class ToolsPluginSettingsPage : MyPageRight
{
    private static readonly string[] SortNames =
    [
        "文件名",
        "资源名",
        "标签数量",
        "添加时间",
        "文件大小"
    ];

    private MyComboBox _sortBox = null!;
    private Image _skinImage = null!;
    private TextBlock _skinStatusText = null!;
    private TextBlock _skinPathText = null!;
    private string? _skinAddress;
    private bool _restartButtonShown;
    private bool _suppress;

    public ToolsPluginSettingsPage()
    {
        var scroll = new MyScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var panel = new StackPanel { Margin = new Thickness(25, 10, 25, 10) };
        scroll.Content = panel;
        Child = scroll;
        PanScroll = scroll;

        panel.Children.Add(BuildCard());
        panel.Children.Add(BuildFeaturesCard());

        // 页面实例会被缓存，每次进入时同步一次控件值（例如资源页改过排序）。
        PageEnter += RefreshValues;
    }

    private MyCard BuildCard()
    {
        var settings = PluginSettings.Current;

        var card = new MyCard
        {
            Title = PluginConstants.Name,
            Margin = new Thickness(0, 15, 0, 15),
            UseAnimation = false
        };

        var root = new StackPanel { Margin = new Thickness(25, 40, 25, 20) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = "NbcPluginLabel"
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        for (var i = 0; i < 6; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 行 1：排序记忆
        AddLabel(grid, 1, "排序记忆");
        _sortBox = new MyComboBox
        {
            Width = 180,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        foreach (var name in SortNames)
            _sortBox.Items.Add(new MyComboBoxItem { Content = name });
        _sortBox.SelectedIndex = NormalizeSortIndex(settings.CompSortMethod);
        Place(grid, _sortBox, 1, 1);
        // 初值在挂事件前设置，刷新时用 Suppress 屏蔽，值未变化也不写盘。
        _sortBox.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            var index = _sortBox.SelectedIndex;
            if (index is < 0 or > 4) return;
            var current = PluginSettings.Current;
            if (current.CompSortMethod == index) return;
            current.CompSortMethod = index;
            PluginSettings.Save();
        };

        // 行 0：当前离线档案的头像预览与文件位置
        AddLabel(grid, 0, "头像预览");
        Place(grid, BuildSkinPreview(), 0, 1);

        // 行 2：更新选项
        AddLabel(grid, 2, "更新选项");
        Place(grid, BuildUpdateOptionsRow(), 2, 1);

        // 行 3-5：插件信息
        var info = ReadPluginInfo();
        AddInfo(grid, 3, "版本", info.Version);
        AddInfo(grid, 4, "PCL Core 版本", info.CoreVersion);
        AddInfo(grid, 5, "Mixin 数量", info.MixinCount);

        root.Children.Add(grid);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var updateButton = new MyButton
        {
            Text = "检查更新",
            Width = 140,
            Height = 35,
            Margin = new Thickness(0, 0, 10, 0)
        };
        updateButton.Click += async (_, _) =>
        {
            updateButton.IsEnabled = false;
            try
            {
                await PluginUpdate.CheckNowAsync();
            }
            finally
            {
                updateButton.IsEnabled = true;
            }
        };
        buttonPanel.Children.Add(updateButton);

        var readmeButton = new MyButton
        {
            Text = "牢大生存指南",
            Width = 140,
            Height = 35,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = PluginUpdate.ReadmeUrl
        };
        readmeButton.Click += (_, _) => OpenReadme();
        buttonPanel.Children.Add(readmeButton);

        var openButton = new MyButton
        {
            Text = "打开插件数据目录",
            Width = 140,
            Height = 35,
            ToolTip = PluginSettings.DataDirectory
        };
        openButton.Click += (_, _) => OpenDataDirectory();
        buttonPanel.Children.Add(openButton);

        root.Children.Add(buttonPanel);

        card.Children.Add(root);
        return card;
    }

    private FrameworkElement BuildUpdateOptionsRow()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var checkUpdate = new MyCheckBox
        {
            Text = "检查更新",
            Checked = PluginSettings.Current.IsFeatureEnabled("updateCheck"),
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        checkUpdate.Change += (_, user) =>
        {
            if (!user) return;
            var settings = PluginSettings.Current;
            settings.EnabledFeatures["updateCheck"] = checkUpdate.Checked == true;
            PluginSettings.Save();
            ShowPclRestartButton();
        };
        panel.Children.Add(checkUpdate);

        var checkNotes = new MyCheckBox
        {
            Text = "显示更新内容弹窗",
            Checked = PluginSettings.Current.IsFeatureEnabled("updateNotes"),
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        checkNotes.Change += (_, user) =>
        {
            if (!user) return;
            var settings = PluginSettings.Current;
            settings.EnabledFeatures["updateNotes"] = checkNotes.Checked == true;
            PluginSettings.Save();
            ShowPclRestartButton();
        };
        panel.Children.Add(checkNotes);

        return panel;
    }

    private MyCard BuildFeaturesCard()
    {
        var card = new MyCard
        {
            Title = "功能开关",
            Margin = new Thickness(0, 0, 0, 15),
            UseAnimation = false
        };

        var root = new StackPanel { Margin = new Thickness(25, 40, 25, 20) };

        foreach (var feature in FeatureCatalog.Features)
        {
            var check = new MyCheckBox
            {
                Text = feature.Name,
                Checked = PluginSettings.Current.IsFeatureEnabled(feature.Key),
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var key = feature.Key;
            check.Change += (_, user) =>
            {
                if (!user) return;
                var settings = PluginSettings.Current;
                settings.EnabledFeatures[key] = check.Checked == true;
                PluginSettings.Save();
                ShowPclRestartButton();
            };
            root.Children.Add(check);
        }

        card.Children.Add(root);
        return card;
    }

    /// <summary>
    /// 复用 PCL 顶部的重启按钮。功能开关不会改变插件启用状态，
    /// 因此 HasPendingRestartChanges 不会自动为 true，这里手动让按钮显示。
    /// </summary>
    private void ShowPclRestartButton()
    {
        if (_restartButtonShown) return;

        try
        {
            if (ModMain.frmMain is null) return;
            ModMain.frmMain.RefreshRestartButton(true);
            if (ModMain.frmMain.FindName("BtnExtraUpdateRestart") is MyExtraButton button)
            {
                button.Show = true;
                _restartButtonShown = true;
            }
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("显示重启按钮失败：" + ex);
        }
    }

    private Grid BuildSkinPreview()
    {
        var container = new Grid();
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        container.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        _skinImage = new Image
        {
            Width = 56,
            Height = 56,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 10)
        };
        RenderOptions.SetBitmapScalingMode(_skinImage, BitmapScalingMode.NearestNeighbor);
        _skinImage.MouseLeftButtonUp += (_, _) => CopySkinAddress();
        container.Children.Add(_skinImage);

        _skinStatusText = new TextBlock { Text = string.Empty, Margin = new Thickness(0, 0, 0, 4) };
        _skinPathText = new TextBlock
        {
            Text = string.Empty,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        _skinPathText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        textStack.Children.Add(_skinStatusText);
        textStack.Children.Add(_skinPathText);
        Grid.SetColumn(textStack, 1);
        container.Children.Add(textStack);

        return container;
    }

    private void RefreshValues()
    {
        _suppress = true;
        try
        {
            var settings = PluginSettings.Current;
            var index = NormalizeSortIndex(settings.CompSortMethod);
            if (_sortBox.SelectedIndex != index)
                _sortBox.SelectedIndex = index;

            RefreshSkinPreview();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RefreshSkinPreview()
    {
        try
        {
            var profile = ModProfile.selectedProfile;
            if (profile is null)
            {
                ShowSkinPreview(null, "未选择档案", string.Empty, null);
                return;
            }

            if (profile.Type != ModLaunch.McLoginType.Legacy)
            {
                ShowSkinPreview(null, "当前档案不是离线档案，无法预览本地皮肤", string.Empty, null);
                return;
            }

            var address = OfflineSkinMixin.ResolveSkinAddress(profile.Username, profile.Uuid);
            var isCustom = !address.StartsWith(ModBase.pathImage, StringComparison.Ordinal);
            if (isCustom)
            {
                ShowSkinPreview(
                    BuildHead(address),
                    "自定义皮肤",
                    Path.GetFileNameWithoutExtension(address),
                    address);
                return;
            }

            var fileName = Path.GetFileName(address);
            ShowSkinPreview(
                BuildHead(address),
                "未选择自定义皮肤，使用默认皮肤",
                Path.GetFileNameWithoutExtension(fileName),
                null);
        }
        catch (Exception ex)
        {
            _skinImage.Source = null;
            _skinStatusText.Text = "皮肤预览加载失败";
            _skinPathText.Text = string.Empty;
            _skinPathText.ToolTip = null;
            PluginBootstrap.WriteLog("皮肤预览加载失败：" + ex);
        }
    }

    private void ShowSkinPreview(MyBitmap? head, string status, string path, string? toolTip)
    {
        _skinAddress = toolTip;
        _skinImage.Source = head;
        _skinImage.ToolTip = toolTip;
        _skinImage.Cursor = toolTip is null ? Cursors.Arrow : Cursors.Hand;
        _skinStatusText.Text = status;
        _skinPathText.Text = path;
    }

    private void CopySkinAddress()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_skinAddress)) return;
            Clipboard.SetText(_skinAddress);
            HintService.Hint("皮肤地址已复制", HintType.Success);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("复制头像地址失败：" + ex);
        }
    }

    private static MyBitmap? BuildHead(string address)
    {
        var image = new MyBitmap(address);
        var scale = image.PixelWidth / 64;
        if (image.PixelWidth < 64 || image.PixelHeight < 32 ||
            image.PixelWidth % 64 != 0 ||
            (image.PixelHeight != image.PixelWidth && image.PixelHeight != image.PixelWidth / 2))
            return null;

        var face = image.Clip(scale * 8, scale * 8, scale * 8, scale * 8);
        var head = MyBitmap.Create(56, 56).Overlay(face.Scale(48, 48), 4, 4);
        if (image.PixelHeight >= 64)
        {
            var hat = image.Clip(scale * 40, scale * 8, scale * 8, scale * 8);
            head = head.Overlay(hat.Scale(56, 56), 0, 0);
        }

        return head;
    }

    private static int NormalizeSortIndex(int value) => value is >= 0 and <= 4 ? value : 1;

    private static void AddLabel(Grid grid, int row, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 25, 10)
        };
        Place(grid, label, row, 0);
    }

    private static void AddInfo(Grid grid, int row, string label, string value)
    {
        AddLabel(grid, row, label);
        var block = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        Place(grid, block, row, 1);
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private readonly record struct PluginInfo(string Version, string CoreVersion, string MixinCount);

    private static PluginInfo ReadPluginInfo()
    {
        var root = ResolvePluginRoot();
        var version = "未知";
        var coreVersion = "未知";
        var mixinCount = "未知";

        try
        {
            using var doc = ParseJsonFile(Path.Combine(root, "plugin.json"));
            version = ReadString(doc.RootElement, "version") ?? version;
            coreVersion = ReadString(doc.RootElement, "pclCoreVersion") ?? coreVersion;
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("读取 plugin.json 失败：" + ex.Message);
        }

        try
        {
            using var doc = ParseJsonFile(Path.Combine(root, "mixins.json"));
            if (doc.RootElement.TryGetProperty("mixins", out var mixins) &&
                mixins.ValueKind == JsonValueKind.Array)
                mixinCount = mixins.GetArrayLength() + " 个";
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("读取 mixins.json 失败：" + ex.Message);
        }

        return new PluginInfo(version, coreVersion, mixinCount);
    }

    private static JsonDocument ParseJsonFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolvePluginRoot()
    {
        var installed = Path.Combine(Paths.Plugins, PluginConstants.Id);
        if (File.Exists(Path.Combine(installed, "plugin.json")))
            return installed;

        // 扁平安装/开发目录回退：程序集可能在插件根目录或 lib 子目录。
        var assemblyDirectory = Path.GetDirectoryName(typeof(PluginBootstrap).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            if (File.Exists(Path.Combine(assemblyDirectory, "plugin.json")))
                return assemblyDirectory;

            var parent = Path.GetDirectoryName(assemblyDirectory);
            if (!string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "plugin.json")))
                return parent;
        }

        return installed;
    }

    private static void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(PluginSettings.DataDirectory);
            Basics.OpenPath(PluginSettings.DataDirectory);
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("打开插件数据目录失败：" + ex);
        }
    }

    private static void OpenReadme()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = PluginUpdate.ReadmeUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("打开插件说明失败：" + ex);
        }
    }
}
