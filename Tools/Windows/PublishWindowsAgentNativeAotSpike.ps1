[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The standalone Agent Native AOT spike must be published on Windows.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI is unavailable.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$spikeRoot = Join-Path $repositoryRoot 'artifacts\publish\Agent-NativeAot-Spike'
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path $spikeRoot $runId
$buildDirectory = Join-Path $runDirectory 'build'
$publishDirectory = Join-Path $runDirectory 'agent'
$binaryLog = Join-Path $runDirectory 'publish.binlog'
$agentProject = Join-Path $repositoryRoot 'Windows\Agent\PasswordManagerLocal.Windows.Agent.csproj'

[void][IO.Directory]::CreateDirectory($runDirectory)
$publishArguments = @(
    'publish', $agentProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $publishDirectory,
    "-bl:$binaryLog"
)

Write-Host "Publishing the standalone Agent Native AOT spike into '$runDirectory'."
$previousArtifactsPath = $env:PmlWindowsAgentAotArtifactsPath
Push-Location -LiteralPath $repositoryRoot
try {
    $env:PmlWindowsAgentAotArtifactsPath = $buildDirectory
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Agent Native AOT publish failed with exit code $LASTEXITCODE. See '$binaryLog'."
    }
}
finally {
    $env:PmlWindowsAgentAotArtifactsPath = $previousArtifactsPath
    Pop-Location
}

$executableName = 'PasswordManagerLocal.Windows.Agent.exe'
$executablePath = Join-Path $publishDirectory $executableName
foreach ($name in @($executableName, 'sqlite3mc.dll', 'libsodium.dll', 'PasswordManagerLocal.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name) -PathType Leaf)) {
        throw "The standalone Agent publish is missing '$name'. See '$binaryLog'."
    }
}

$unexpectedFiles = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse -Force | Where-Object {
    ($_.Extension -eq '.exe' -and $_.FullName -ne $executablePath) -or
    $_.Name -in @('coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll') -or
    $_.Name -like 'PasswordManagerLocal*.dll' -or
    $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json'
}
if ($unexpectedFiles) {
    throw "Unexpected files in the standalone Agent publish: $($unexpectedFiles.Name -join ', '). See '$binaryLog'."
}

Write-Host "Standalone Agent executable: $executablePath"
Write-Host "Publish diagnostics: $binaryLog"
Write-Host 'Run this executable only in a disposable Windows account or VM. See Docs\WINDOWS_AGENT_NATIVE_AOT_SPIKE.md.'
