param([switch]$TestOnly, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) { $dotnet = $localDotnet }
elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { $dotnet = (Get-Command dotnet).Source }
else { throw '请先安装 .NET 10 SDK，或把 SDK 放入 .tools\dotnet。只安装运行时无法编译。' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if (!(Test-Path -LiteralPath 'src\DianXu.App\Assets\DianXu.ico')) { & (Join-Path $PSScriptRoot 'New-Icon.ps1') }
if (!$SkipTests) {
    & $dotnet build 'tests\DianXu.Tests\DianXu.Tests.csproj' -c Release --nologo --disable-build-servers -m:1 -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '项目编译失败。' }
    & $dotnet 'tests\DianXu.Tests\bin\Release\net10.0-windows\DianXu.Tests.dll' (Join-Path $root '.test-data\core')
    if ($LASTEXITCODE -ne 0) { throw '自动测试失败，未发布。' }
}
if ($TestOnly) { return }
$publish = Join-Path $root 'artifacts\publish'
& $dotnet publish 'src\DianXu.App\DianXu.App.csproj' -c Release -r win-x64 --self-contained true -o $publish --nologo --disable-build-servers -m:1 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '打包失败。' }
$release = Join-Path $root 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'DianXu.exe') -Destination (Join-Path $release '点序.exe') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\使用说明.txt') -Destination (Join-Path $release '使用说明.txt') -Force
# Build a clean source archive, without downloaded SDKs, caches, or compiled output.
Add-Type -AssemblyName System.IO.Compression
$zipStream = [System.IO.File]::Open((Join-Path $release '点序-源码.zip'), [System.IO.FileMode]::Create)
$archive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root 'src'), (Join-Path $root 'tests'), (Join-Path $root 'scripts'), (Join-Path $root 'docs') -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $files += Get-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'Directory.Build.props'), (Join-Path $root 'NuGet.Config'), (Join-Path $root '.gitignore')
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose(); $zipStream.Dispose() }
$hash = (Get-FileHash -LiteralPath (Join-Path $release '点序.exe') -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText((Join-Path $release 'SHA256.txt'), "$hash  点序.exe" + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath $release | Select-Object Name, Length
