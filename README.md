# NiButCrazy 奇妙特性

PCL Nex 的 Mixin 插件，用于补全官方发布版中缺失的离线皮肤功能：

1. **离线皮肤**：不再只有 Steve / Alex。支持本地自定义皮肤，并让默认头像从 9 个官方皮肤中稳定选择。
2. **离线换肤**：离线档案的「修改皮肤」菜单可以直接选择本地 PNG。
3. **头像逻辑**：重写 `MySkin.Load`，只读取皮肤文件并截取头像区域，绝不写回来源文件。

4. **下载搜索词继承**：在本地 Mod/资源管理页输入搜索词后点击「下载新资源」，下载页会自动填入并搜索。
5. **资源排序记忆**：在 Mod 管理页选择排序方式后，插件会记住该选择，重新加载或切换实例时自动恢复。

> 动画帧率问题已由上游 `713128b` 修复（垂直同步档位跟随显示器刷新率），插件不再处理帧率。

## 构建

插件需要引用 PCL Nex 的 `PCL.Core.dll` 和 `Plain Craft Launcher 2.dll`。
默认从同级目录的 PCL2-Nex 仓库读取：

```
E:\MyCode\CSharpProjects\
├─ PCL2-Nex\                         ← 先在主仓库构建 Release/x64
└─ NiButCrazy-WonderfulFeatures\     ← 本仓库
```

先在主仓库构建：

```powershell
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64
```

然后在本仓库构建：

```powershell
.\build-plugin.ps1
```

如果 PCL2-Nex 不在默认位置，可以指定输出目录：

```powershell
.\build-plugin.ps1 -PclBuildDir "D:\Somewhere\PCL2-Nex\Plain Craft Launcher 2\bin\Release\net10.0-windows\win-x64"
```

产物在 `dist/nibutcrazy.wonderful-features/`。

## 打包格式

`.pclx` 和 `.zip` 指向同一种东西：**ZIP 压缩包**。

- `.pclx` 是 PCL 插件专用扩展名，便于系统关联和插件市场识别。
- 启动器的本地安装同时接受 `.pclx` 和 `.zip`，解压后会递归查找 `plugin.json`。
- 目录形式（解压后的 `dist/nibutcrazy.wonderful-features/`）则是安装后的形态，适合手动放进 `PCL/Plugins`。

`build-plugin.ps1` 会同时生成：

```
dist/nibutcrazy.wonderful-features/       目录（可直接放入 PCL/Plugins）
dist/nibutcrazy.wonderful-features.pclx   插件包（推荐分发）
dist/nibutcrazy.wonderful-features.zip    同样内容
```

## 安装

### 一键安装到本地启动器

```powershell
.\install-plugin.ps1
```

默认目标为 `D:\MC\nex\PCL2.exe`，会构建插件、复制到该启动器的 `PCL/Plugins/`，并在
`PCL/config.local.v1.json` 中自动启用。其他位置可以用参数覆盖：

```powershell
.\install-plugin.ps1 -PclPath "E:\Somewhere\PCL2.exe"
```

### 手动安装

推荐在启动器中打开 **设置 → 插件 → 从本地安装**，选择生成的 `.pclx` 或 `.zip`。

也可以手动把 `dist/nibutcrazy.wonderful-features` 目录放到启动器程序目录下的：

```
PCL/Plugins/nibutcrazy.wonderful-features/
├─ plugin.json
├─ mixins.json
└─ lib/
   └─ PCL.Plugin.NiButCrazy.dll
```

然后在启动器中打开 **设置 → 插件**（或对应插件管理页面），启用「NiButCrazy 奇妙特性」。
插件启用状态保存在程序目录的 `PCL/config.local.v1.json`。

也可以直接把插件压缩成 zip，通过启动器的本地插件安装功能安装。

## 自定义离线皮肤

### 在启动器中直接换肤

选中离线档案，然后点击 **皮肤与披风 → 修改皮肤**，选择本地 PNG 文件。
插件会把它复制到下面的 `Skin` 目录并立即刷新头像，不再显示「当前档案不支持修改皮肤！」。

### 手动放置皮肤文件

首次启动后插件会创建：

```
PCL/Plugins/data/nibutcrazy.wonderful-features/
├─ config.json
├─ plugin.log
└─ Skin/                ← 必须是单数，匹配 PCL 的头像缓存解析
   ├─ 说明.txt
   └─ <玩家 ID>.png    ← 把你的皮肤放到这里
```

> 1.1.1 之前版本使用 `Skins/`（复数），会让 `MySkin.Load` 把整条绝对路径当成头像 ID，
> 进而把头像写回皮肤文件，表现为「头像变成一坨色块」。
> 新版启动时会自动把旧的 `Skins/` 重命名为 `Skins.old-<时间戳>`，请重新选择一次皮肤。

皮肤必须是 Minecraft 格式的 PNG（64×64 或 64×32）。
支持三种命名，按以下优先级匹配：

1. `<玩家 ID>.png`
2. `<离线 UUID>.png`
3. `default.png`

都没有时，从 9 个官方默认皮肤（Steve、Alex、Ari、Efe、Kai、Makena、Noor、Sunny、Zuri）中按 UUID 稳定选择。

### 头像逻辑（1.2.0）

插件重写了 `MySkin.Load`：

- 直接读取皮肤文件，按分辨率截取头部 8×8 区域（含帽子层）
- 头像缓存使用皮肤地址的 SHA-256 作为文件名，只写入
  `%TEMP%\PCLNex\Cache\Skin\Head\`
- 不再解析路径标记，任何皮肤路径都不会被误当成缓存目标
- 来源皮肤文件全程只读

## 配置

`config.json`：

```json
{
  "VariedDefaultSkins": true
}
```

- `VariedDefaultSkins`：`true` 时使用 9 个默认皮肤；`false` 时恢复为 Steve/Alex。

## 注意

- 插件通过 Mixin 修改 `PageLaunchLeft.SkinLegacyLoad`、`PageLoginProfileSkin.Skin_Click` 和 `MySkin.Load`。
  PCL Nex 更新后如果这些方法签名变化，插件会加载失败并被启动器自动禁用；此时删除插件或等待适配即可。
