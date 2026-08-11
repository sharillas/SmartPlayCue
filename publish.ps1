# ============================================================
# StagePlayout - build de producao
# 1) Publish self-contained single-file (.exe pronto, sem .NET necessario)
# 2) Se o Inno Setup estiver instalado, gera o instalador em dist\
# ============================================================
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Write-Host "A publicar (self-contained, single-file)..." -ForegroundColor Cyan
& $dotnet publish "$PSScriptRoot\src\StagePlayout.App" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$PSScriptRoot\publish"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish falhou - ver erros acima." -ForegroundColor Red
    exit 1
}

# FFmpeg nativo: copiar explicitamente (PublishSingleFile nao honra CopyToPublishDirectory)
Write-Host "A copiar FFmpeg nativo..." -ForegroundColor Cyan
Copy-Item "$PSScriptRoot\thirdparty\FFmpeg" "$PSScriptRoot\publish\FFmpeg" -Recurse -Force

Write-Host "Publish OK -> publish\" -ForegroundColor Green

$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Write-Host "A gerar instalador com Inno Setup..." -ForegroundColor Cyan
    & $iscc "$PSScriptRoot\installer\setup.iss"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Instalador criado em dist\" -ForegroundColor Green
    }
} else {
    Write-Host "Inno Setup nao encontrado. Instala com: winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
    Write-Host "(O .exe standalone esta em publish\ e ja funciona em qualquer PC Windows)"
}
