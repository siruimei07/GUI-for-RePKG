[CmdletBinding()]
param(
    [ValidateSet('Report', 'Verify')]
    [string] $Mode = 'Report',

    [string] $ProjectPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $ProjectPath = Join-Path $repositoryRoot 'ThirdParty/RePKG/Source/RePKG.Application/RePKG.Application.csproj'
}

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$applicationRoot = Split-Path -Parent $resolvedProject

function Normalize-CompilePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return $Path.Replace('/', '\')
}

function Get-EvaluatedCompileItems {
    param([Parameter(Mandatory = $true)][string] $Path)

    $output = & dotnet msbuild $Path -getItem:Compile -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild could not evaluate Compile items for $Path (exit $LASTEXITCODE)."
    }

    $evaluation = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    return @($evaluation.Items.Compile | ForEach-Object {
        Normalize-CompilePath -Path ([string] $_.Identity)
    } | Sort-Object -Unique)
}

function Get-SourceBaseline {
    param([Parameter(Mandatory = $true)][string] $Root)

    $prefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            Normalize-CompilePath -Path $_.FullName.Substring($prefix.Length)
        } |
        Sort-Object -Unique)
}

$baseline = @(Get-SourceBaseline -Root $applicationRoot)

# This is a role-based whitelist: keep shared roots, exceptions, and TEX read/
# convert code; exclude the eager package API and every TEX writer/compressor.
$candidate = @($baseline | Where-Object {
    $_ -eq 'Constants.cs' `
        -or $_ -eq 'Extensions.cs' `
        -or $_.StartsWith('Exceptions\', [StringComparison]::OrdinalIgnoreCase) `
        -or ($_.StartsWith('Texture\', [StringComparison]::OrdinalIgnoreCase) `
            -and -not $_.StartsWith('Texture\Writer\', [StringComparison]::OrdinalIgnoreCase))
} | Sort-Object -Unique)

$excluded = @($baseline | Where-Object { $candidate -notcontains $_ })
$evaluated = @(Get-EvaluatedCompileItems -Path $resolvedProject)
$missing = @($candidate | Where-Object { $evaluated -notcontains $_ })
$unexpected = @($evaluated | Where-Object { $candidate -notcontains $_ })
$reduction = $baseline.Count - $candidate.Count

$coreProject = Join-Path (Split-Path -Parent $applicationRoot) 'RePKG.Core/RePKG.Core.csproj'
$coreRoot = Split-Path -Parent $coreProject
$coreBaseline = @(Get-SourceBaseline -Root $coreRoot)
$coreEvaluated = @(Get-EvaluatedCompileItems -Path $coreProject)
$coreMissing = @($coreBaseline | Where-Object { $coreEvaluated -notcontains $_ })
$coreUnexpected = @($coreEvaluated | Where-Object { $coreBaseline -notcontains $_ })

Write-Output ("REPKG_COMPILE_SURFACE baseline={0} candidate={1} reduction={2} evaluated={3} core_baseline={4} core_evaluated={5} core_policy=unchanged" -f `
    $baseline.Count, $candidate.Count, $reduction, $evaluated.Count, $coreBaseline.Count, $coreEvaluated.Count)
Write-Output ('REPKG_COMPILE_EXCLUDED count={0} files="{1}"' -f `
    $excluded.Count, ($excluded -join ';'))
Write-Output ('REPKG_COMPILE_MISSING count={0} files="{1}"' -f `
    $missing.Count, ($missing -join ';'))
Write-Output ('REPKG_COMPILE_UNEXPECTED count={0} files="{1}"' -f `
    $unexpected.Count, ($unexpected -join ';'))
Write-Output ('REPKG_CORE_MISSING count={0} files="{1}"' -f `
    $coreMissing.Count, ($coreMissing -join ';'))
Write-Output ('REPKG_CORE_UNEXPECTED count={0} files="{1}"' -f `
    $coreUnexpected.Count, ($coreUnexpected -join ';'))

if ($Mode -eq 'Verify') {
    if ($reduction -le 0) {
        throw 'The RePKG.Application whitelist does not reduce the compile input set.'
    }

    if ($missing.Count -ne 0 -or $unexpected.Count -ne 0) {
        throw 'The evaluated RePKG.Application Compile items do not match the role-based whitelist.'
    }

    if ($coreMissing.Count -ne 0 -or $coreUnexpected.Count -ne 0) {
        throw 'The evaluated RePKG.Core Compile items no longer match the unchanged full source surface.'
    }
}
