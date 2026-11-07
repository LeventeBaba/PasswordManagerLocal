[CmdletBinding()]
param(
    [string]$AgentStage
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The Windows Native AOT prototype must be published on Windows.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($AgentStage)) {
    $AgentStage = Join-Path $repositoryRoot 'artifacts\publish\PasswordManagerLocal.Windows-ReadyToRun\staging\agent'
}
if (-not (Test-Path -LiteralPath $AgentStage -PathType Container)) {
    throw "Agent stage is missing at '$AgentStage'. Run publish.cmd -W -ReadyToRun first or pass -AgentStage."
}

$agentDirectory = (Get-Item -LiteralPath $AgentStage).FullName
$agentPrefix = $agentDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$agentFiles = @(
    'PasswordManagerLocal.Windows.Agent.exe',
    'PasswordManagerLocal.Windows.Agent.dll',
    'PasswordManagerLocal.Windows.Agent.deps.json',
    'PasswordManagerLocal.Windows.Agent.runtimeconfig.json',
    'coreclr.dll'
)
foreach ($name in $agentFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $agentDirectory $name) -PathType Leaf)) {
        throw "Agent stage is missing '$name' at '$agentDirectory'."
    }
}
if (Test-Path -LiteralPath (Join-Path $agentDirectory 'PasswordManagerLocal.exe')) {
    throw 'Pass the standalone staging\agent directory to -AgentStage.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI is unavailable.'
}

$prototypeRoot = Join-Path $repositoryRoot 'artifacts\publish\PasswordManagerLocal.Windows-NativeAot'
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path $prototypeRoot $runId
$buildDirectory = Join-Path $runDirectory 'build'
$stagingDirectory = Join-Path $runDirectory 'staging'
$productDirectory = Join-Path $runDirectory 'product'
$frontendProject = Join-Path $repositoryRoot 'Windows\Frontend\PasswordManagerLocal.Windows.Frontend.csproj'
$publishArguments = @(
    'publish', $frontendProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--artifacts-path', $buildDirectory,
    '--output', $stagingDirectory,
    '-p:PmlWindowsProductStaging=true',
    '-p:PmlWindowsFrontendNativeAot=true'
)

Write-Host "Publishing the Windows frontend with Native AOT into '$runDirectory'."
Write-Host "Reusing the managed Agent stage at '$agentDirectory'."
Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT frontend publish failed with exit code $LASTEXITCODE. Check the publish diagnostics above."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory 'PasswordManagerLocal.exe') -PathType Leaf) -or
    (Test-Path -LiteralPath (Join-Path $stagingDirectory 'PasswordManagerLocal.dll'))) {
    throw 'The Native AOT publish did not produce the expected frontend executable layout.'
}

# Both executables locate their sibling in the same directory. Preserve the
# Agent stage byte for byte and accept shared paths only when the files match.
foreach ($file in Get-ChildItem -LiteralPath $agentDirectory -Recurse -File -Force) {
    $relativePath = $file.FullName.Substring($agentPrefix.Length)
    $destination = Join-Path $stagingDirectory $relativePath
    if (Test-Path -LiteralPath $destination) {
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
            throw "Native AOT frontend and Agent files differ at '$relativePath'."
        }
        continue
    }

    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}

[IO.Directory]::Move($stagingDirectory, $productDirectory)
Write-Host "Native AOT prototype executable: $(Join-Path $productDirectory 'PasswordManagerLocal.exe')"
