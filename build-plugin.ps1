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

Write-Host "完成：$dist" -ForegroundColor Green
Write-Host '把整个目录放到 <启动器目录>/PCL/Plugins/ 下，并在插件页启用。' -ForegroundColor Green
