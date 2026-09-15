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
# o modulo tem de levar TODAS as deps transitivas dentro do tgz.
# Resolvemos via registry npm (sem precisar de npm instalado).
$nm = Join-Path $stage "package\node_modules"
$script:Seen = @{}

function Add-Pkg {
    param([string]$Name, [string]$Range)
    $key = "$Name@$Range"
    if ($script:Seen.ContainsKey($key)) { return }
    $script:Seen[$key] = $true

    $view = (& npm view "$Name@$Range" version dist.tarball dependencies --json 2>$null | Out-String) | ConvertFrom-Json
    if ($null -eq $view) { throw "npm view falhou para $Name@$Range" }
    $ver = @($view.version)[-1]
    $tarball = @($view.'dist.tarball')[-1]
    Write-Host "  dep: $Name@$ver"

    $dest = Join-Path $nm ($Name -replace '/', '\')
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $tgz = Join-Path $env:TEMP ("spc-dep-" + ($Name -replace '[^a-z0-9]', '-') + "-$ver.tgz")
    Invoke-WebRequest $tarball -OutFile $tgz
    Push-Location $dest
    try {
        tar -xzf $tgz --strip-components 1
        if ($LASTEXITCODE -ne 0) { throw "tar falhou em $Name" }
    } finally {
        Pop-Location
        Remove-Item $tgz -Force -ErrorAction SilentlyContinue
    }

    $deps = @($view.dependencies)[-1]
    if ($null -ne $deps) {
        foreach ($d in $deps.PSObject.Properties) {
            Add-Pkg $d.Name $d.Value
        }
    }
}

New-Item -ItemType Directory -Force -Path $nm | Out-Null
$baseVer = (([string]$pkg.dependencies.'@companion-module/base') -replace '[~^]', '').Trim()
Add-Pkg "@companion-module/base" $pkg.dependencies.'@companion-module/base'

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
