#requires -Version 5.1
<#
    Сборка LongCat Minecraft Launcher в ОДИН автономный .exe
    Результат: .\билды exe\Launcher.exe
#>

$ErrorActionPreference = 'Stop'

$root     = $PSScriptRoot
$project  = Join-Path $root 'src\MCLauncher\MCLauncher.csproj'
$outDir   = Join-Path $root 'билды exe'
$tempPub  = Join-Path $env:TEMP 'mclauncher_publish'

Write-Host ''
Write-Host '=== LongCat Launcher :: сборка Single-File EXE ===' -ForegroundColor Green
Write-Host ''

# --- 1. Проверяем наличие dotnet SDK ------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue

if (-not $dotnet) {
    $candidates = @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $dotnet = $c; break }
    }
}
else { $dotnet = $dotnet.Source }

if (-not $dotnet) {
    Write-Host '.NET SDK не найден. Устанавливаю .NET 8 SDK...' -ForegroundColor Yellow

    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel 8.0 -Quality GA -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"

    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
}

Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray
& $dotnet --version

# --- 2. Чистим предыдущие артефакты -------------------------------------
if (Test-Path $tempPub) { Remove-Item $tempPub -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- 3. Публикация -------------------------------------------------------
Write-Host ''
Write-Host 'Публикую (self-contained, single-file, win-x64)...' -ForegroundColor Cyan
Write-Host ''

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:GenerateDocumentationFile=false `
    -o $tempPub

if ($LASTEXITCODE -ne 0) {
    Write-Host 'Сборка завершилась с ошибкой.' -ForegroundColor Red
    exit $LASTEXITCODE
}

# --- 4. Копируем только exe в папку результата --------------------------
$exe = Join-Path $tempPub 'MaysLauncher.exe'
if (-not (Test-Path $exe)) { throw "MaysLauncher.exe не найден в $tempPub" }

Copy-Item $exe -Destination (Join-Path $outDir 'MaysLauncher.exe') -Force

$size = [math]::Round((Get-Item (Join-Path $outDir 'MaysLauncher.exe')).Length / 1MB, 1)

Write-Host ''
Write-Host '=== ГОТОВО ===' -ForegroundColor Green
Write-Host ("Файл:   " + (Join-Path $outDir 'MaysLauncher.exe'))
Write-Host ("Размер: $size МБ")
Write-Host 'Запускается на любом ПК с Windows x64 без установленного .NET.' -ForegroundColor DarkGray
Write-Host ''
