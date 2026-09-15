param([string]$OutFile = "smartplaycue-2.6.2.tgz")
$ErrorActionPreference = "Stop"

# O Companion exige que o tar tenha uma ENTRADA DE DIRETÓRIO raiz (`package/`)
# como primeiro elemento. O `npm pack` NÃO a inclui, por isso o import falha
# com "missing manifest". Este script monta o mesmo layout npm mas com o tar
# clássico (bsdtar inclui as entradas de diretório).
#
# Nota: bsdtar no Windows trata backslashes como escapes e falha com paths
# absolutos — tudo aqui usa paths RELATIVOS ao módulo.
$stage = Join-Path $PSScriptRoot ".pack-stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $stage "package") | Out-Null

Copy-Item main.js, presets.js, README.md, package.json -Destination (Join-Path $stage "package")
Copy-Item companion -Destination (Join-Path $stage "package") -Recurse

Push-Location $stage
try {
    tar -czf "../$OutFile" package
    if ($LASTEXITCODE -ne 0) { throw "tar falhou ($LASTEXITCODE)" }
} finally {
    Pop-Location
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "criado $OutFile"
