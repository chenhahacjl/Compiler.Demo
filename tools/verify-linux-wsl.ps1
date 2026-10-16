param(
    [Parameter(Mandatory = $true)]
    [string]$ElfPath,
    [Parameter(Mandatory = $false)]
    [string]$InputText = ""
)

# T3 gate 3 (WSL 本地版)：在 WSL2 下运行 linux-x64 原生产物（无需 VM）。
# Windows 路径自动翻译为 /mnt/<drive>/...；InputText 经 WSL printf 注入 stdin（\n 按换行解释）。
# 退出码 0 = 干净退出；stdout 原样流出。

if (-not (Test-Path -LiteralPath $ElfPath)) {
    Write-Output "error: $ElfPath not found"
    exit 1
}

$wslPath = $ElfPath.Replace('\', '/')
if ($wslPath -match '^([A-Za-z]):') {
    $wslPath = '/mnt/' + $matches[1].ToLower() + $wslPath.Substring(2)
}

$rc = 0
if ($InputText -eq "") {
    wsl sh -c "chmod +x '$wslPath'; '$wslPath'"
    $rc = $LASTEXITCODE
}
else {
    # 输入作 printf 格式串（\n 由 bash 解释为换行；输入仅数字/字母场景）；经管道进 stdin
    wsl sh -c "chmod +x '$wslPath'; printf '$InputText' | '$wslPath'"
    $rc = $LASTEXITCODE
}

if ($rc -eq 0) {
    Write-Output "COCOA_VERIFY_OK"
    exit 0
}

Write-Output "COCOA_VERIFY_FAIL: $ElfPath exited $rc"
exit 1