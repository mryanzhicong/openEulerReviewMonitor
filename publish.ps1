param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
& $Dotnet publish "$PSScriptRoot/ForumReviewMonitor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PSScriptRoot/dist/win-x64"
if ($LASTEXITCODE -ne 0) { throw '发布失败' }
Copy-Item "$PSScriptRoot/README.md" "$PSScriptRoot/dist/win-x64/使用说明.md" -Force
Write-Host "输出目录：$PSScriptRoot/dist/win-x64"
