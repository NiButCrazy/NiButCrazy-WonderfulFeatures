[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PclBuildDir = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'NiButCrazy.WonderfulFeatures.csproj'
$dist = Join-Path $root 'dist\nibutcrazy.wonderful-features'

$buildArgs = @($project, '-c', $Configuration, "-p:Platform=$Platform")
if ($PclBuildDir) {
    $buildArgs += "-p:PclBuildDir=$PclBuildDir"
}

Write-Host "构建插件（$Configuration/$Platform）..." -ForegroundColor Cyan
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }

$assembly = Join-Path $root "bin\$Configuration\PCL.Plugin.NiButCrazy.dll"
if (-not (Test-Path -LiteralPath $assembly)) {
    throw "未找到插件程序集：$assembly"
}

Write-Host "组装插件包到 $dist ..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $dist 'lib') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $root 'plugin.json') -Destination $dist
Copy-Item -LiteralPath (Join-Path $root 'mixins.json') -Destination $dist
Copy-Item -LiteralPath $assembly -Destination (Join-Path $dist 'lib')

$deps = Join-Path $root "bin\$Configuration\PCL.Plugin.NiButCrazy.deps.json"
if (Test-Path -LiteralPath $deps) {
    Copy-Item -LiteralPath $deps -Destination (Join-Path $dist 'lib')
}

# 生成可分发的插件包。.pclx 与 .zip 内容完全相同，前者是 PCL 的专用扩展名。
$distRoot = Join-Path $root 'dist'
$pclx = Join-Path $distRoot 'nibutcrazy.wonderful-features.pclx'
$zip = Join-Path $distRoot 'nibutcrazy.wonderful-features.zip'
foreach ($archive in @($pclx, $zip)) {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $dist,
    $pclx,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Copy-Item -LiteralPath $pclx -Destination $zip

Write-Host "完成：$dist" -ForegroundColor Green
Write-Host "插件包：$pclx" -ForegroundColor Green
Write-Host "    或：$zip" -ForegroundColor Green
Write-Host '安装：启动器「设置 → 插件 → 从本地安装」选择 .pclx / .zip，或把目录直接放到 <启动器目录>/PCL/Plugins/。' -ForegroundColor Green
