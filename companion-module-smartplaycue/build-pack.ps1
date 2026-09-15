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

# Bundling das dependencias (o Companion 5 nao instala deps de modulos importados):
# o modulo tem de levar o @companion-module/base (e o colord) dentro do tgz.
$nm = Join-Path $stage "package\node_modules"
New-Item -ItemType Directory -Force -Path $nm | Out-Null
$scoped = Join-Path $nm "@companion-module"
New-Item -ItemType Directory -Force -Path $scoped | Out-Null
Copy-Item node_modules\@companion-module\base -Destination $scoped -Recurse
if (Test-Path node_modules\colord) {
    Copy-Item node_modules\colord -Destination $nm -Recurse
}

# O build oficial preenche a versao do manifest a partir do package.json.
# Fazemos o mesmo aqui (sem BOM para o JSON.parse do Companion).
# O apiVersion fica como esta no companion/manifest.json (1.14.0 = formato
# legacy suportado pelo Companion 3.3+ ate 5.x; 0.0.0 torna o modulo invisivel).
$manifestPath = Join-Path $stage "package\companion\manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $pkg.version
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
