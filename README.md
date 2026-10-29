# NiButCrazy 奇妙特性

PCL Nex 的 Mixin 插件，以模组列表加载、筛选切换与排序体验优化为主，同时补全离线皮肤、中文搜索等启动器功能。

当前版本：**1.7.12**

## 功能

### 资源管理体验

- **下载搜索词继承**：在本地 Mod/资源管理页输入搜索词后点击「下载新资源」，下载页会自动填入并搜索。
- **排序记忆**：在 Mod 管理页选择排序方式后自动保存，重新加载、切换实例或首次进入时恢复。
- **中文搜索增强**：读取内置的 Mod 翻译库，把「应用能源」这类中文查询转换为公共英文关键词（如 `applied energistics`），避免只锁定到 AE1 而搜不到 AE2。
- **Mod 列表加载优化**：控件分批创建，加载动画保持到列表完全就绪；筛选 Tab 切换采用增量更新和分帧刷新，减少大量 Mod 时的卡顿。
- **搜索取消异常修复**：不再反复输出 `CancellationTokenSource has been disposed` 日志。

### 离线皮肤与头像

- **离线皮肤**：不再只有 Steve / Alex。支持本地自定义皮肤，默认头像从 9 个官方皮肤中按 UUID 稳定选择。
- **离线换肤**：离线档案的「皮肤与披风 → 修改皮肤」可以直接选择本地 PNG，不再提示「当前档案不支持修改皮肤！」。
- **只读头像逻辑**：重写 `MySkin.Load`，只读取皮肤文件并截取头部区域，绝不写回来源皮肤文件。
- **头像过渡**：头像淡入（360ms），帽子层延迟 60ms；昵称和账号类型同步淡入。
- **启动优化**：启动阶段复用人像缓存并延迟皮肤处理，避免和窗口入场动画抢 UI 线程。

## 安装

### 插件市场（推荐）

仓库带有 `pclnexplugin` Topic，启动器的插件市场会自动发现。搜索「NiButCrazy 奇妙特性」，或在「实用」分组中查找。

> 已经能通过 Topic 发现时，不要再手动添加同一个插件的 Manifest 源，否则市场会把同一插件按不同来源显示成多条。

### 一键安装到本地启动器

```powershell
.\install-plugin.ps1
```

默认目标为 `D:\MC\nex\PCL2.exe`，会构建插件、复制到 `PCL/Plugins/` 并自动启用。其他位置可以覆盖：

```powershell
.\install-plugin.ps1 -PclPath "E:\Somewhere\PCL2.exe"
```

### 手动安装

在启动器中打开 **设置 → 插件 → 从本地安装**，选择 `.pclx` 或 `.zip`。

也可以把解压后的目录放到：

```
PCL/Plugins/nibutcrazy.wonderful-features/
├─ plugin.json
├─ mixins.json
├─ assets/icon.png
└─ lib/PCL.Plugin.NiButCrazy.dll
```

然后在插件管理页面启用「NiButCrazy 奇妙特性」。启用状态保存在 `PCL/config.local.v1.json` 的 `PluginEnabledStates`。

## 自定义离线皮肤

> **注意：本插件的离线皮肤只影响启动器内的头像和皮肤预览，不会修改游戏内皮肤。**
> 进游戏后要显示皮肤，仍需要服务端插件（如 SkinsRestorer）或客户端 Mod（如 CustomSkinLoader）配合。

### 在启动器中换肤

选中离线档案 → **皮肤与披风 → 修改皮肤** → 选择本地 PNG。
插件会复制到下面的 `Skin` 目录并立即刷新头像。

### 手动放置皮肤文件

首次启动后插件会创建：

```
PCL/Plugins/data/nibutcrazy.wonderful-features/
├─ config.json
├─ plugin.log
└─ Skin/                ← 必须是单数
   ├─ 说明.txt
   └─ <玩家 ID>.png
```

命名按以下优先级匹配：

1. `<玩家 ID>.png`
2. `<离线 UUID>.png`
3. `default.png`

皮肤要求为 Minecraft 格式的 PNG（64×64 或 64×32）。都没有匹配时，从 Steve、Alex、Ari、Efe、Kai、Makena、Noor、Sunny、Zuri 中按 UUID 稳定选择。

> 1.1.1 之前版本使用 `Skins/`（复数），会把头像缓存写回皮肤文件，表现为「头像变成一坨色块」。
> 新版启动时会自动把旧的 `Skins/` 重命名为 `Skins.old-<时间戳>`，请重新选择一次皮肤。

## 配置

`PCL/Plugins/data/nibutcrazy.wonderful-features/config.json`：

```json
{
  "VariedDefaultSkins": true,
  "CompSortMethod": 1
}
```

- `VariedDefaultSkins`：`true` 使用 9 个默认皮肤；`false` 恢复为 Steve/Alex。
- `CompSortMethod`：资源管理的默认排序方式。
  - `0` 文件名
  - `1` 资源名（默认）
  - `2` 标签数量
  - `3` 添加时间
  - `4` 文件大小

## 构建

插件需要引用 PCL Nex 的 `PCL.Core.dll` 和 `Plain Craft Launcher 2.dll`。推荐把本仓库与 `PCL2-Nex` 放在同一级目录：

```
<workspace>/
├─ PCL2-Nex/
└─ NiButCrazy-WonderfulFeatures/
```

先在主仓库构建 Release/x64：

```powershell
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64
```

然后在本仓库构建：

```powershell
.\build-plugin.ps1
```

主仓库不在默认位置时：

```powershell
.\build-plugin.ps1 -PclBuildDir "D:\Somewhere\PCL2-Nex\Plain Craft Launcher 2\bin\Release\net10.0-windows\win-x64"
```

产物：

```
dist/nibutcrazy.wonderful-features/       可直接放入 PCL/Plugins 的目录
dist/nibutcrazy.wonderful-features.pclx   插件包（推荐分发）
dist/nibutcrazy.wonderful-features.zip    同样内容
```

`.pclx` 与 `.zip` 内容完全相同，前者是 PCL 插件专用扩展名。

## 开发说明

插件通过 `PCL.Mixin` 修改以下方法：

| 目标 | 方式 | 作用 |
| --- | --- | --- |
| `PageLaunchLeft.SkinLegacyLoad` | Overwrite | 离线皮肤解析与默认皮肤选择 |
| `PageLoginProfileSkin.Skin_Click` | Inject HEAD | 离线档案的修改皮肤入口 |
| `MySkin.Load` | Overwrite | 只读皮肤、截取头像、哈希缓存、淡入过渡、启动阶段延迟 |
| `PageLoginProfileSkin.Reload` | Inject HEAD | 刷新前先把昵称/账号类型设为透明，避免闪烁 |
| `ModComp.CompProjectsGet` | Inject HEAD | 中文搜索关键词改写 |
| `PageInstanceCompResource.BtnManageDownload_Click` | Inject HEAD | 记录下载搜索词 |
| `PageComp.PageCompControls_Inited` | Inject RETURN | 下载页填充搜索词 |
| `PageInstanceCompResource.BtnSortClick` | Inject HEAD | 标记用户选择排序 |
| `PageInstanceCompResource.SetSortMethod` | Inject TAIL | 保存/恢复排序 |
| `PageInstanceCompResource.SearchRun` | Overwrite | 修复 CTS 释放竞态 |
| `PageInstanceCompResource.LoadUIFromLoaderOutput` | Overwrite | 分批创建 Mod 控件并保持加载动画 |
| `PageInstanceCompResource.RefreshUI` | Overwrite | 增量刷新、分帧插入与格式化 |
| `PageInstanceCompResource.RefreshBars` | Inject HEAD | 批次加载期间抑制计数刷新 |

PCL Nex 更新后如果这些方法签名变化，插件会加载失败并被启动器自动禁用，`plugin.log` 中会记录原因。

## 市场发布

- `manifest.json` 位于仓库根目录，声明插件元数据、版本、平台包和 SHA-256。
- Release tag 使用 `v<版本号>`，资产命名 `nibutcrazy.wonderful-features-windows-anycpu.pclx`。
- 发版流程：修改 `plugin.json` 版本 → 构建 → 更新 `manifest.json` 的版本/URL/哈希 → 提交推送 → 创建对应 Release。
