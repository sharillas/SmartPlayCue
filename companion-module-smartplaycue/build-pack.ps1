param([string]$OutFile = "")
$ErrorActionPreference = "Stop"

$pkg = Get-Content package.json -Raw | ConvertFrom-Json
if ($OutFile.Length -eq 0) { $OutFile = "smartplaycue-$($pkg.version).tgz" }

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

# O build oficial preenche a versao e o apiVersion do manifest a partir do
# package.json. Fazemos o mesmo aqui (sem BOM para o JSON.parse do Companion).
# IMPORTANTE: apiVersion tem de ser COMPATIVEL com o Companion instalado
# (ex.: 2.1.x para o Companion 5.0.5) — 0.0.0 torna o modulo invisivel na lista.
$manifestPath = Join-Path $stage "package\companion\manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $pkg.version
$baseDep = [string]$pkg.dependencies.'@companion-module/base'
$manifest.runtime.apiVersion = ($baseDep -replace '[~^]', '').Trim()
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10))

Push-Location $stage
try {
    tar -czf "../$OutFile" package
    if ($LASTEXITCODE -ne 0) { throw "tar falhou ($LASTEXITCODE)" }
} finally {
    Pop-Location
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "criado $OutFile"
