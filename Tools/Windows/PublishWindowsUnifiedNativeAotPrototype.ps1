#Requires -Version 5.1
[CmdletBinding()]
param([switch]$Sign)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The unified Windows Native AOT prototype must be published on Windows.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI is unavailable.'
}

. (Join-Path $PSScriptRoot 'WindowsReleaseSigning.ps1')
$signingContext = if ($Sign) { Get-WindowsReleaseSigningContext }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$prototypeRoot = Join-Path $repositoryRoot 'artifacts\publish\PasswordManagerLocal.Windows-UnifiedNativeAot'
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path $prototypeRoot $runId
$buildDirectory = Join-Path $runDirectory 'build'
$stagingDirectory = Join-Path $runDirectory 'staging'
$productDirectory = Join-Path $runDirectory 'product'
$binaryLog = Join-Path $runDirectory 'publish.binlog'
$frontendProject = Join-Path $repositoryRoot 'Windows\Frontend\PasswordManagerLocal.Windows.Frontend.csproj'

[void][IO.Directory]::CreateDirectory($runDirectory)
$publishArguments = @(
    'publish', $frontendProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $stagingDirectory,
    '-p:PmlWindowsProductStaging=true',
    "-bl:$binaryLog"
)

Write-Host "Publishing the unified Windows Native AOT prototype into '$runDirectory'."
$previousArtifactsPath = $env:PmlWindowsUnifiedAotArtifactsPath
Push-Location -LiteralPath $repositoryRoot
try {
    $env:PmlWindowsUnifiedAotArtifactsPath = $buildDirectory
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Unified Native AOT publish failed with exit code $LASTEXITCODE. See '$binaryLog'."
    }
}
finally {
    $env:PmlWindowsUnifiedAotArtifactsPath = $previousArtifactsPath
    Pop-Location
}

Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse -Filter '*.pdb' | Remove-Item -Force
$expectedFiles = @(
    'PasswordManagerLocal.exe',
    'av_libglesv2.dll',
    'libHarfBuzzSharp.dll',
    'libSkiaSharp.dll',
    'libsodium.dll',
    'sqlite3mc.dll'
)
foreach ($name in $expectedFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $name) -PathType Leaf)) {
        throw "The unified Native AOT publish is missing '$name'. See '$binaryLog'."
    }
}
$unexpectedFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -Force | Where-Object {
    $_.PSIsContainer -or $_.Name -notin $expectedFiles
})
if ($unexpectedFiles.Count -gt 0) {
    throw "Unexpected files in the unified Native AOT publish: $($unexpectedFiles.Name -join ', '). See '$binaryLog'."
}

if ($Sign) {
    Invoke-WindowsProductSigning -ProductDirectory $stagingDirectory -Context $signingContext -UnifiedNativeAot `
        -ReportPath (Join-Path $runDirectory 'windows-signing-report.json')
}

[IO.Directory]::Move($stagingDirectory, $productDirectory)
Write-Host "Unified Native AOT executable: $(Join-Path $productDirectory 'PasswordManagerLocal.exe')"
Write-Host "Publish diagnostics: $binaryLog"
Write-Host 'Run this prototype only in a disposable Windows account or VM.'
