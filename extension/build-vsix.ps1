$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

Write-Host "=== Cocoa VS Code Extension Packager ===" -ForegroundColor Cyan

Write-Host "Checking Node.js..."
$nodeVersion = node --version
if ($LASTEXITCODE -ne 0) {
    Write-Host "Node.js is not installed. Please install Node.js 16+." -ForegroundColor Red
    exit 1
}
Write-Host "Node.js $nodeVersion" -ForegroundColor Green

Write-Host "Installing dependencies..."
npm install
if ($LASTEXITCODE -ne 0) {
    Write-Host "npm install failed." -ForegroundColor Red
    exit 1
}
Write-Host "Dependencies installed." -ForegroundColor Green

Write-Host "Compiling TypeScript..."
npx tsc -p ./
if ($LASTEXITCODE -ne 0) {
    Write-Host "TypeScript compilation failed." -ForegroundColor Red
    exit 1
}
Write-Host "Compilation successful." -ForegroundColor Green

Write-Host "Removing old .vsix..."
Remove-Item -LiteralPath "*.vsix" -Force -ErrorAction SilentlyContinue

Write-Host "Packaging extension..."
npx -y @vscode/vsce package
if ($LASTEXITCODE -ne 0) {
    Write-Host "Packaging failed." -ForegroundColor Red
    exit 1
}

$vsixFile = Get-ChildItem -Filter "*.vsix" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($vsixFile) {
    Write-Host "=== Generated: $($vsixFile.Name) ===" -ForegroundColor Green
}
