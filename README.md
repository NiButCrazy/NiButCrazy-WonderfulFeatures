# NiButCrazy的奇妙特性

PCL Nex 的 Mixin 插件，用于绕过官方发布版中写死的两个限制：

1. **离线皮肤**：不再只有 Steve / Alex。支持本地自定义皮肤，并让默认头像从 9 个官方皮肤中稳定选择。
2. **动画帧率**：把 `UiAniFPS` 配置自动设置为 240（跟随显示器刷新率），不再固定在 60 FPS。

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

然后在启动器中打开 **设置 → 插件**（或对应插件管理页面），启用「NiButCrazy的奇妙特性」。
插件启用状态保存在程序目录的 `PCL/config.local.v1.json`。

也可以直接把插件压缩成 zip，通过启动器的本地插件安装功能安装。

## 自定义离线皮肤

### 在启动器中直接换肤

选中离线档案，然后点击 **皮肤与披风 → 修改皮肤**，选择本地 PNG 文件。
插件会把它复制到下面的 `Skins` 目录并立即刷新头像，不再显示「当前档案不支持修改皮肤！」。

### 手动放置皮肤文件

首次启动后插件会创建：

```
PCL/Plugins/data/nibutcrazy.wonderful-features/
├─ config.json
├─ plugin.log
└─ Skins/
   ├─ 说明.txt
   └─ <玩家 ID>.png    ← 把你的皮肤放到这里
```

皮肤必须是 Minecraft 格式的 PNG（64×64 或 64×32）。
支持三种命名，按以下优先级匹配：

1. `<玩家 ID>.png`
2. `<离线 UUID>.png`
3. `default.png`

都没有时，从 9 个官方默认皮肤（Steve、Alex、Ari、Efe、Kai、Makena、Noor、Sunny、Zuri）中按 UUID 稳定选择。

## 配置

`config.json`：

```json
{
  "AnimationFps": 240,
  "VariedDefaultSkins": true
}
```

- `AnimationFps`：启动时写入的动画帧率配置，`0` 表示不修改。
- `VariedDefaultSkins`：`true` 时使用 9 个默认皮肤；`false` 时恢复为 Steve/Alex。

## 注意

- 设置页滑块仍然显示 60 FPS 上限，但实际动画会按配置值运行。
- 插件通过 Mixin 替换 `PageLaunchLeft.SkinLegacyLoad`，PCL Nex 更新后如果该方法签名变化，插件会加载失败并被启动器自动禁用；此时删除插件或等待适配即可。
