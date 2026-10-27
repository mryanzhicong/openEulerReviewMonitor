param([string]$Dotnet = 'dotnet', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$publishArgs = @('publish', "$PSScriptRoot/ForumReviewMonitor.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', "$PSScriptRoot/dist/win-x64")
if ($NoRestore) { $publishArgs += '--no-restore' }
& $Dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw '发布失败' }
$version = ([xml](Get-Content -Raw "$PSScriptRoot/ForumReviewMonitor.csproj")).Project.PropertyGroup.Version
$releaseName = "openEulerReviewMonitor-v$version-win-x64.exe"
Move-Item -LiteralPath "$PSScriptRoot/dist/win-x64/openEulerReviewMonitor.exe" -Destination "$PSScriptRoot/dist/win-x64/$releaseName" -Force
Copy-Item "$PSScriptRoot/README.md" "$PSScriptRoot/dist/win-x64/使用说明.md" -Force
Write-Host "发布文件：$PSScriptRoot/dist/win-x64/$releaseName"
