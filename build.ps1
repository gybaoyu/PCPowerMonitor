# 编译 PowerMonitor.cs -> build\PowerMonitor.exe （使用系统自带 .NET Framework 4.x 编译器，无需安装 SDK）
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$sources = @(Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'src 下没有 .cs 文件' }
$manifest = Join-Path $root 'src\app.manifest'
$ico = Join-Path $root 'assets\app.ico'
$outDir = Join-Path $root 'build'
$exe = Join-Path $outDir 'PowerMonitor.exe'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (-not (Test-Path $ico)) { & (Join-Path $root 'tools\make-icon.ps1') }

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    '/codepage:65001'
    ('/out:' + $exe)
    ('/win32icon:' + $ico)
    ('/win32manifest:' + $manifest)
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
)
$cscArgs += $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }

$info = Get-Item $exe
Write-Host ("编译完成: {0}  ({1:N1} KB)" -f $info.FullName, ($info.Length / 1KB))
