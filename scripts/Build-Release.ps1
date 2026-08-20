[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PrivateKeyPath = "",
    [string]$InnoSetupPath = "",
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

if ([string]::IsNullOrWhiteSpace($InnoSetupPath)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    )
    $InnoSetupPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoSetupPath) -or -not (Test-Path -LiteralPath $InnoSetupPath)) {
    throw "未找到 Inno Setup 6 编译器 ISCC.exe。"
}

$releaseRoot = Join-Path $projectRoot "temp\release\v$version"
$liteDirectory = Join-Path $releaseRoot "package-lite"
$fullDirectory = Join-Path $releaseRoot "package-full"
$workRoot = Join-Path $releaseRoot "work"
$litePackageName = "PinNote-$version-Lite-Portable.zip"
$fullPackageName = "PinNote-$version-Full-Portable.zip"
$liteSetupName = "PinNote-$version-Lite-Setup"
$fullSetupName = "PinNote-$version-Full-Setup"
$litePackagePath = Join-Path $releaseRoot $litePackageName
$fullPackagePath = Join-Path $releaseRoot $fullPackageName
$liteManifestPath = Join-Path $releaseRoot "update-lite.json"
$fullManifestPath = Join-Path $releaseRoot "update-full.json"
$compatibilityManifestPath = Join-Path $releaseRoot "update.json"
$liteChannel = "portable-framework-dependent"
$fullChannel = "portable-self-contained"

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $liteDirectory, $fullDirectory, $workRoot -Force | Out-Null

if (-not $SkipBuild) {
    dotnet build-server shutdown | Out-Null
    dotnet build PinNote.sln --configuration $Configuration --disable-build-servers --maxcpucount:1 --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "Release 构建失败。" }
    $env:PINNOTE_TEST_TEMP = Join-Path $projectRoot "temp\tests"
    dotnet run --project tests\PinNote.SmokeTests\PinNote.SmokeTests.csproj --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "冒烟测试失败。" }
}

foreach ($project in @("src\PinNote\PinNote.csproj", "src\PinNote.Updater\PinNote.Updater.csproj")) {
    dotnet restore $project --runtime win-x64 --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "win-x64 发布依赖还原失败：$project" }
}

dotnet publish src\PinNote\PinNote.csproj --configuration $Configuration --runtime win-x64 `
    --self-contained false --no-restore --output $liteDirectory
if ($LASTEXITCODE -ne 0) { throw "Lite 主程序发布失败。" }

$liteUpdaterDirectory = Join-Path $workRoot "updater-lite"
dotnet publish src\PinNote.Updater\PinNote.Updater.csproj --configuration $Configuration --runtime win-x64 `
    --self-contained false --no-restore --output $liteUpdaterDirectory
if ($LASTEXITCODE -ne 0) { throw "Lite 更新器发布失败。" }
foreach ($fileName in @("PinNote.Updater.exe", "PinNote.Updater.dll", "PinNote.Updater.deps.json", "PinNote.Updater.runtimeconfig.json")) {
    Copy-Item -LiteralPath (Join-Path $liteUpdaterDirectory $fileName) -Destination (Join-Path $liteDirectory $fileName)
}

dotnet publish src\PinNote\PinNote.csproj --configuration $Configuration --runtime win-x64 `
    --self-contained true --no-restore --output $fullDirectory
if ($LASTEXITCODE -ne 0) { throw "Full 主程序发布失败。" }

$fullUpdaterDirectory = Join-Path $workRoot "updater-full"
dotnet publish src\PinNote.Updater\PinNote.Updater.csproj --configuration $Configuration --runtime win-x64 `
    --self-contained true --no-restore --output $fullUpdaterDirectory `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "Full 更新器发布失败。" }
Copy-Item -LiteralPath (Join-Path $fullUpdaterDirectory "PinNote.Updater.exe") `
    -Destination (Join-Path $fullDirectory "PinNote.Updater.exe") -Force

foreach ($item in @(
    @{ Directory = $liteDirectory; Channel = $liteChannel },
    @{ Directory = $fullDirectory; Channel = $fullChannel }
)) {
    Get-ChildItem -LiteralPath $item.Directory -Filter "*.pdb" | Remove-Item -Force
    $metadataPath = Join-Path $item.Directory "pinnote-package.json"
    dotnet run --project tools\PinNote.ReleaseTool\PinNote.ReleaseTool.csproj `
        --configuration $Configuration --no-build -- metadata `
        --version $version --channel $item.Channel --output $metadataPath
    if ($LASTEXITCODE -ne 0) { throw "包元数据生成失败：$($item.Channel)" }
    Copy-Item -LiteralPath $metadataPath -Destination (Join-Path $item.Directory "pinnote-install.json")
}

Compress-Archive -Path (Join-Path $liteDirectory "*") -DestinationPath $litePackagePath -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $fullDirectory "*") -DestinationPath $fullPackagePath -CompressionLevel Optimal

$issPath = Join-Path $projectRoot "installer\PinNote.iss"
& $InnoSetupPath "/DAppVersion=$version" "/DSourceDir=$liteDirectory" "/DOutputDir=$releaseRoot" `
    "/DOutputBaseFilename=$liteSetupName" "/DRequireDesktopRuntime=1" $issPath
if ($LASTEXITCODE -ne 0) { throw "Lite Setup 生成失败。" }
& $InnoSetupPath "/DAppVersion=$version" "/DSourceDir=$fullDirectory" "/DOutputDir=$releaseRoot" `
    "/DOutputBaseFilename=$fullSetupName" $issPath
if ($LASTEXITCODE -ne 0) { throw "Full Setup 生成失败。" }

if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $resolvedKey = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
    foreach ($item in @(
        @{ Package = $litePackagePath; Name = $litePackageName; Channel = $liteChannel; Manifest = $liteManifestPath },
        @{ Package = $fullPackagePath; Name = $fullPackageName; Channel = $fullChannel; Manifest = $fullManifestPath }
    )) {
        $downloadUrl = "https://github.com/Kratosmax/PinNote/releases/download/v$version/$($item.Name)"
        dotnet run --project tools\PinNote.ReleaseTool\PinNote.ReleaseTool.csproj `
            --configuration $Configuration --no-build -- manifest `
            --version $version --channel $item.Channel --package $item.Package --private-key $resolvedKey `
            --download-url $downloadUrl --release-notes RELEASE_NOTES.md --output $item.Manifest
        if ($LASTEXITCODE -ne 0) { throw "签名更新清单生成失败：$($item.Channel)" }
    }
    Copy-Item -LiteralPath $liteManifestPath -Destination $compatibilityManifestPath
}

$assetNames = @(
    "$liteSetupName.exe", "$fullSetupName.exe", $litePackageName, $fullPackageName,
    "update.json", "update-lite.json", "update-full.json"
)
$hashLines = foreach ($assetName in $assetNames) {
    $assetPath = Join-Path $releaseRoot $assetName
    if (Test-Path -LiteralPath $assetPath) {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash, $assetName
    }
}
$hashLines | Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding ascii

$assets = foreach ($assetName in $assetNames) {
    $assetPath = Join-Path $releaseRoot $assetName
    if (Test-Path -LiteralPath $assetPath) {
        [ordered]@{
            name = $assetName
            size = (Get-Item -LiteralPath $assetPath).Length
            sha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
        }
    }
}
[ordered]@{ version = $version; assets = @($assets) } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $releaseRoot "release-evidence.json") -Encoding utf8

Write-Host "Release 产物：$releaseRoot"
