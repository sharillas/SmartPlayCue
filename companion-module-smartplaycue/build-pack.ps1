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
# Descarregamos direto do registry npm — sem depender de node_modules local.
function Extract-NpmPkg {
    param([string]$TgzUrl, [string]$DestDir)
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
    $tmpTgz = Join-Path $DestDir "_pkg.tgz"
    Invoke-WebRequest $TgzUrl -OutFile $tmpTgz
    Push-Location $DestDir
    try {
        tar -xzf "_pkg.tgz" --strip-components 1
        if ($LASTEXITCODE -ne 0) { throw "tar falhou ($LASTEXITCODE) em $DestDir" }
    } finally {
        Pop-Location
        Remove-Item $tmpTgz -Force -ErrorAction SilentlyContinue
    }
}

$nm = Join-Path $stage "package\node_modules"
$baseVer = (([string]$pkg.dependencies.'@companion-module/base') -replace '[~^]', '').Trim()
Extract-NpmPkg "https://registry.npmjs.org/@companion-module/base/-/base-$baseVer.tgz" (Join-Path $nm "@companion-module\base")
Extract-NpmPkg "https://registry.npmjs.org/colord/-/colord-2.9.3.tgz" (Join-Path $nm "colord")

# O build oficial preenche a versao do manifest a partir do package.json.
# Fazemos o mesmo aqui (sem BOM para o JSON.parse do Companion).
# O apiVersion fica como esta no companion/manifest.json (1.14.0 = formato
# legacy suportado pelo Companion 3.3+ ate 5.x; 0.0.0 torna o modulo invisivel).
$manifestPath = Join-Path $stage "package\companion\manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $pkg.version
$manifest.name = "Smartchoice: Smart PlayCue.v$($pkg.version) [ by Nelson Teixeira ]"
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
