# ============================================================
# StagePlayout - loop de desenvolvimento: build + run
# Uso: clique direito -> "Executar com PowerShell" ou:
#   powershell -ExecutionPolicy Bypass -File run.ps1
# ============================================================
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Write-Host "A compilar..." -ForegroundColor Cyan
& $dotnet build "$PSScriptRoot\StagePlayout.sln" -v q --nologo

if ($LASTEXITCODE -eq 0) {
    Write-Host "A iniciar StagePlayout..." -ForegroundColor Green
    Start-Process "$PSScriptRoot\src\StagePlayout.App\bin\Debug\net8.0-windows\StagePixPlay.exe"
} else {
    Write-Host "Build falhou - ver erros acima." -ForegroundColor Red
    exit 1
}
