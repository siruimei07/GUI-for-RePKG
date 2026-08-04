[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'
$projectPath = Join-Path $projectRoot 'WallpaperField.csproj'
$publishedExecutable = Join-Path $publishDirectory 'WallpaperField.exe'
$rootExecutable = Join-Path $projectRoot 'GUI_for_RePKG.exe'

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf))
{
    throw "Publish did not create the expected executable: $publishedExecutable"
}

Copy-Item -LiteralPath $publishedExecutable -Destination $rootExecutable -Force
Get-Item -LiteralPath $rootExecutable
