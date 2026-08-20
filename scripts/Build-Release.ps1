[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PrivateKeyPath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $projectRoot

[xml]$buildProps = Get-Content -Raw -LiteralPath (Join-Path $projectRoot "Directory.Build.props")
$version = [string]$buildProps.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props 中的 Version 必须是三段数字版本。"
}

$releaseRoot = Join-Path $projectRoot "temp\release\v$version"
$packageDirectory = Join-Path $releaseRoot "package"
$packageName = "PinNote-$version-portable-win-x64.zip"
$packagePath = Join-Path $releaseRoot $packageName
$manifestPath = Join-Path $releaseRoot "update.json"

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

if (-not $SkipBuild) {
    dotnet build-server shutdown | Out-Null
    dotnet build PinNote.sln --configuration $Configuration --disable-build-servers --maxcpucount:1 --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "Release 构建失败。" }
    $env:PINNOTE_TEST_TEMP = Join-Path $projectRoot "temp\tests"
    dotnet run --project tests\PinNote.SmokeTests\PinNote.SmokeTests.csproj --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "冒烟测试失败。" }
}

dotnet restore src\PinNote\PinNote.csproj --runtime win-x64 --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw "win-x64 发布依赖还原失败。" }

dotnet publish src\PinNote\PinNote.csproj `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --no-restore `
    --output $packageDirectory
if ($LASTEXITCODE -ne 0) { throw "PinNote 发布失败。" }

$updaterOutput = Join-Path $projectRoot "temp\build\PinNote.Updater\$Configuration\net8.0-windows"
foreach ($fileName in @(
    "PinNote.Updater.exe",
    "PinNote.Updater.dll",
    "PinNote.Updater.deps.json",
    "PinNote.Updater.runtimeconfig.json"
)) {
    $source = Join-Path $updaterOutput $fileName
    if (-not (Test-Path -LiteralPath $source)) { throw "更新器缺少 $fileName。" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageDirectory $fileName)
}
Get-ChildItem -LiteralPath $packageDirectory -Filter "*.pdb" | Remove-Item -Force

$packageMetadata = Join-Path $packageDirectory "pinnote-package.json"
dotnet run --project tools\PinNote.ReleaseTool\PinNote.ReleaseTool.csproj `
    --configuration $Configuration --no-build -- `
    metadata --version $version --output $packageMetadata
if ($LASTEXITCODE -ne 0) { throw "包元数据生成失败。" }
Copy-Item -LiteralPath $packageMetadata -Destination (Join-Path $packageDirectory "pinnote-install.json")

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $packagePath -CompressionLevel Optimal

if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $resolvedKey = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
    $downloadUrl = "https://github.com/Kratosmax/PinNote/releases/download/v$version/$packageName"
    dotnet run --project tools\PinNote.ReleaseTool\PinNote.ReleaseTool.csproj `
        --configuration $Configuration --no-build -- `
        manifest --version $version --package $packagePath --private-key $resolvedKey `
        --download-url $downloadUrl --release-notes RELEASE_NOTES.md --output $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "签名更新清单生成失败。" }
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
"$hash  $packageName" | Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding ascii
[ordered]@{
    version = $version
    package = $packagePath
    size = (Get-Item -LiteralPath $packagePath).Length
    sha256 = $hash
    manifest = if (Test-Path -LiteralPath $manifestPath) { $manifestPath } else { $null }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot "release-evidence.json") -Encoding utf8

Write-Host "Release 产物：$releaseRoot"
