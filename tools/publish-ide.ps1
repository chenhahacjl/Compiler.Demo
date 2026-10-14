# Publish Cocoa.IDE with VS/dotnet-style layout: Compiler DLLs under .\Compiler, SDK under .\SDK.
# Usage: powershell -ExecutionPolicy Bypass -File tools\publish-ide.ps1
# Output: tools\cocoa-ide-dist\  (Cocoa.IDE.exe + Compiler\ + SDK\)
$ErrorActionPreference = 'Stop'
$ROOT = Split-Path -Parent $PSScriptRoot
$IDE_PROJ = Join-Path $ROOT 'src\Cocoa.IDE\Cocoa.IDE.csproj'
$OUT = Join-Path $ROOT 'tools\cocoa-ide-dist'
$SDK_SRC = Join-Path $ROOT 'tools\cocoa-sdk'

Write-Host "==> Publish Cocoa.IDE (net9.0, framework-dependent)"
Remove-Item -Recurse -Force $OUT -ErrorAction SilentlyContinue
# 直接发布到 $OUT 根避免子目录再上移（管道重定向 stdout，保留退出码检查）
& dotnet publish $IDE_PROJ --nologo -c Debug -o $OUT 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 1) Compiler: move Cocoa.* DLLs (except the IDE host) into .\Compiler (dotnet sdk/ layout).
#    Host stays at root so Cocoa.IDE.exe finds its Cocoa.IDE.dll beside it.
$COMPILER = Join-Path $OUT 'Compiler'
New-Item -ItemType Directory -Path $COMPILER -Force | Out-Null
Get-ChildItem $OUT -Filter 'Cocoa.*.dll' | Where-Object { $_.Name -ne 'Cocoa.IDE.dll' } | Move-Item -Destination $COMPILER -Force

# 2) SDK: copy System*.coa into .\SDK (dotnet shared-framework layout)
$SDK = Join-Path $OUT 'SDK'
New-Item -ItemType Directory -Path $SDK -Force | Out-Null
if (Test-Path $SDK_SRC) {
    Get-ChildItem $SDK_SRC -Filter '*.coa' | Copy-Item -Destination $SDK -Force
}

Write-Host ""
Write-Host "==> 发布完成：$OUT"
Write-Host "   $OUT\Cocoa.IDE.exe      （宿主）"
Write-Host "   $OUT\Compiler\           （编译器程序集 Cocoa.*.dll）"
Write-Host "   $OUT\SDK\                （系统库 System*.coa）"
Write-Host "   运行：$OUT\Cocoa.IDE.exe"
Write-Host "   SystemLibrary 自动发现 .\SDK；编译器经 ALC 从 .\Compiler 加载"