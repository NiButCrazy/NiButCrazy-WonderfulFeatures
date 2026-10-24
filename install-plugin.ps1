[CmdletBinding()]
param(
    [string]$PclPath = 'D:\MC\nex\PCL2.exe',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PclBuildDir = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pluginId = 'nibutcrazy.wonderful-features'

if (-not (Test-Path -LiteralPath $PclPath)) {
    throw "未找到启动器：$PclPath"
}

$pclDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $PclPath)
$pluginDirectory = Join-Path $pclDirectory "PCL\Plugins\$pluginId"
$distDirectory = Join-Path $root 'dist\nibutcrazy.wonderful-features'

Write-Host "构建插件（$Configuration/$Platform）..." -ForegroundColor Cyan
$buildArgs = @{
    Configuration = $Configuration
    Platform = $Platform
}
if ($PclBuildDir) { $buildArgs.PclBuildDir = $PclBuildDir }
& (Join-Path $root 'build-plugin.ps1') @buildArgs

Write-Host "安装到 $pluginDirectory ..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $pluginDirectory) {
    Remove-Item -LiteralPath $pluginDirectory -Recurse -Force
}
Copy-Item -LiteralPath $distDirectory -Destination $pluginDirectory -Recurse

# 启用插件（保留本地配置中的其他字段）
$configPath = Join-Path $pclDirectory 'PCL\config.local.v1.json'
if (Test-Path -LiteralPath $configPath) {
    Copy-Item -LiteralPath $configPath -Destination "$configPath.bak" -Force
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    [string[]]$states = @($config.PluginEnabledStates) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [string]$_ }
    if ($states -notcontains $pluginId) { $states += $pluginId }
    $config.PluginEnabledStates = [string[]]$states
    $json = $config | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host '插件已启用。' -ForegroundColor Green
} else {
    Write-Host "未找到本地配置文件：$configPath" -ForegroundColor Yellow
    Write-Host '请先启动一次启动器，再到插件页面手动启用。' -ForegroundColor Yellow
}

Write-Host "完成：$pluginDirectory" -ForegroundColor Green
