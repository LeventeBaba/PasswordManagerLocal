[CmdletBinding()]
param(
    [switch]$PrecompileEf
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The standalone Agent Native AOT spike must be published on Windows.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI is unavailable.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$spikeRoot = Join-Path $repositoryRoot 'artifacts\publish\Agent-NativeAot-Spike'
$variant = if ($PrecompileEf) { 'ef-precompiled' } else { 'baseline' }
$runId = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $variant, ([Guid]::NewGuid().ToString('N').Substring(0, 8))
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

Write-Host "Publishing the standalone Agent Native AOT spike ($variant) into '$runDirectory'."
$previousArtifactsPath = $env:PmlWindowsAgentAotArtifactsPath
$previousEfDesignTime = $env:PmlWindowsAgentAotEfDesignTime
$previousEfSourcesPath = $env:PmlWindowsAgentAotEfSourcesPath
$previousConfiguration = $env:Configuration
Push-Location -LiteralPath $repositoryRoot
try {
    $env:PmlWindowsAgentAotEfDesignTime = $null
    $env:PmlWindowsAgentAotEfSourcesPath = $null
    if ($PrecompileEf) {
        $backendProject = Join-Path $repositoryRoot 'Common\Backend\PasswordManagerLocal.Common.Backend.csproj'
        $designBuildLog = Join-Path $runDirectory 'ef-design-build.binlog'
        $efSourcesPath = Join-Path $runDirectory 'ef-sources'
        $efToolPath = Join-Path $runDirectory 'ef-tool'
        $packagesPath = Join-Path $repositoryRoot 'Directory.Packages.props'
        $packages = [xml](Get-Content -LiteralPath $packagesPath -Raw)
        $efVersion = $packages.SelectSingleNode("/Project/ItemGroup/PackageVersion[@Include='Microsoft.EntityFrameworkCore.Design']").GetAttribute('Version')

        # The EF CLI and its Roslyn workspace both evaluate the backend project.
        # Keep their build configuration and artifact path aligned through the environment.
        $env:Configuration = 'Release'
        $env:PmlWindowsAgentAotArtifactsPath = Join-Path $runDirectory 'design'
        $env:PmlWindowsAgentAotEfDesignTime = 'true'
        Write-Host 'Building Common.Backend for EF design-time generation.'
        & dotnet build $backendProject --configuration Release "-bl:$designBuildLog"
        if ($LASTEXITCODE -ne 0) {
            throw "EF design-time backend build failed with exit code $LASTEXITCODE. See '$designBuildLog'."
        }

        & dotnet tool install dotnet-ef --tool-path $efToolPath --version $efVersion
        if ($LASTEXITCODE -ne 0) {
            throw "Installing the isolated EF CLI failed with exit code $LASTEXITCODE."
        }

        $efArguments = @(
            'dbcontext', 'optimize',
            '--project', $backendProject,
            '--startup-project', $backendProject,
            '--configuration', 'Release',
            '--no-build',
            '--context', 'PasswordManagerLocal.Common.Backend.Persistence.AppDbContext',
            '--nativeaot', '--precompile-queries',
            '--output-dir', $efSourcesPath,
            '--namespace', 'PasswordManagerLocal.Common.Backend.Persistence.CompiledModels',
            '--suffix', '.g'
        )
        Write-Host 'Generating the compiled EF model and query interceptors.'
        & (Join-Path $efToolPath 'dotnet-ef.exe') @efArguments
        if ($LASTEXITCODE -ne 0) {
            throw "EF model or query generation failed with exit code $LASTEXITCODE. See the EF CLI output above."
        }

        $generatedSources = @(Get-ChildItem -LiteralPath $efSourcesPath -Recurse -File -Filter '*.cs')
        if (-not ($generatedSources.Name -contains 'AppDbContextModel.g.cs') -or
            -not (Select-String -LiteralPath $generatedSources.FullName -Pattern '[InterceptsLocation(' -SimpleMatch -Quiet)) {
            throw "EF Core did not produce both a compiled AppDbContext model and query interceptors in '$efSourcesPath'."
        }

        $env:PmlWindowsAgentAotEfDesignTime = $null
        $env:PmlWindowsAgentAotEfSourcesPath = $efSourcesPath
    }

    $env:PmlWindowsAgentAotArtifactsPath = $buildDirectory
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Agent Native AOT publish failed with exit code $LASTEXITCODE. See '$binaryLog'."
    }
}
finally {
    $env:PmlWindowsAgentAotArtifactsPath = $previousArtifactsPath
    $env:PmlWindowsAgentAotEfDesignTime = $previousEfDesignTime
    $env:PmlWindowsAgentAotEfSourcesPath = $previousEfSourcesPath
    $env:Configuration = $previousConfiguration
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
