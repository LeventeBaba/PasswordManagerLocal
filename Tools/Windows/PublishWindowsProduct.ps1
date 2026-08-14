[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$OutputRoot,

    [string]$BaselineTreePath,

    [Nullable[long]]$BaselineTotalBytes,

    [switch]$RunSmokeTests
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\publish\PasswordManagerLocal.Windows'
}
if ([string]::IsNullOrWhiteSpace($BaselineTreePath)) {
    $BaselineTreePath = Join-Path $repositoryRoot 'Docs\Baselines\PHASE6_SUPPLIED_WINDOWS_PUBLISH_TREE.txt'
}

$outputRootPath = [System.IO.Path]::GetFullPath($OutputRoot)
$frontendStage = Join-Path $outputRootPath 'staging\frontend'
$agentStage = Join-Path $outputRootPath 'staging\agent'
$productRoot = Join-Path $outputRootPath 'product\PasswordManagerLocal'
$reportRoot = Join-Path $outputRootPath 'reports'
$collisionReportPath = Join-Path $reportRoot 'staged-collision-analysis.json'
$copyUsedAssemblyListPath = Join-Path $reportRoot 'staged-copyused-assemblies.txt'
$initialCopyUsedAssemblyListPath = Join-Path $reportRoot 'staged-copyused-assemblies.initial.txt'
$copyAssemblyListPath = Join-Path $reportRoot 'staged-copy-assemblies.txt'
$zipPath = Join-Path $outputRootPath 'PasswordManagerLocal-win-x64.zip'

$frontendProject = Join-Path $repositoryRoot 'Windows\Frontend\PasswordManagerLocal.Windows.Frontend.csproj'
$agentProject = Join-Path $repositoryRoot 'Windows\Agent\PasswordManagerLocal.Windows.Agent.csproj'
$packagingProject = Join-Path $repositoryRoot 'Windows\Packaging\PasswordManagerLocal.Windows.Packaging.csproj'
$verificationScript = Join-Path $repositoryRoot 'Tools\Windows\VerifyWindowsPublishedLayout.ps1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The dotnet CLI is unavailable.' }
if (Test-Path -LiteralPath $outputRootPath) { Remove-Item -LiteralPath $outputRootPath -Recurse -Force }
New-Item -ItemType Directory -Path $frontendStage, $agentStage, $reportRoot -Force | Out-Null

$commonPublishArguments = @(
    '--configuration', $Configuration,
    '--framework', 'net10.0-windows',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishTrimmed=true',
    '-p:TrimMode=partial',
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false',
    '-p:PublishReferencesSymbols=false'
)

$frontendStageRequiredFiles = @(
    'PasswordManagerLocal.exe',
    'PasswordManagerLocal.dll',
    'PasswordManagerLocal.deps.json',
    'PasswordManagerLocal.runtimeconfig.json',
    'Avalonia.Skia.dll',
    'Avalonia.Win32.dll',
    'PasswordManagerLocal.Common.Contracts.dll',
    'PasswordManagerLocal.Common.Frontend.dll',
    'PasswordManagerLocal.Common.Preferences.dll',
    'PasswordManagerLocal.Windows.EndpointRpc.Client.dll',
    'PasswordManagerLocal.Windows.EndpointRpc.Contracts.dll',
    'PasswordManagerLocal.Windows.Ipc.dll'
)
$agentStageRequiredFiles = @(
    'PasswordManagerLocal.Windows.Agent.exe',
    'PasswordManagerLocal.Windows.Agent.dll',
    'PasswordManagerLocal.Windows.Agent.deps.json',
    'PasswordManagerLocal.Windows.Agent.runtimeconfig.json',
    'PasswordManagerLocal.Common.Backend.dll',
    'PasswordManagerLocal.Common.Backend.Hosting.dll',
    'PasswordManagerLocal.Windows.Backend.dll',
    'PasswordManagerLocal.Windows.EndpointRpc.Server.dll'
)

function Reset-StagingDirectories {
    foreach ($stage in @($frontendStage, $agentStage)) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
    }
}

function Assert-StagedFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string[]]$FileNames,
        [Parameter(Mandatory = $true)][string]$Moment
    )

    $missing = @(
        $FileNames |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $Directory $_) -PathType Leaf) }
    )
    if ($missing.Count -gt 0) {
        throw "$Label staging lost required files $Moment`: $($missing -join ', ')"
    }
}

function Remove-StagedUnownedDlls {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$ManifestFileName,
        [Parameter(Mandatory = $true)][string]$PassName
    )

    $manifestPath = Join-Path $Directory $ManifestFileName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "$Label dependency manifest is missing before DLL ownership cleanup: $manifestPath"
    }

    $safePassName = $PassName -replace '[^A-Za-z0-9._-]', '-'
    $safeLabel = $Label.ToLowerInvariant() -replace '[^a-z0-9._-]', '-'
    $pruneReportPath = Join-Path $reportRoot "staged-unowned-dlls.$safePassName.$safeLabel.txt"
    $pruneArguments = @(
        'run', '--project', $packagingProject,
        '--configuration', $Configuration,
        '--', 'prune-unowned-dlls',
        '--directory', $Directory,
        '--manifest', $manifestPath,
        '--report', $pruneReportPath
    )

    $pruneOutput = & dotnet @pruneArguments
    $pruneExitCode = $LASTEXITCODE
    $pruneOutput | ForEach-Object { Write-Host $_ }
    if ($pruneExitCode -ne 0) {
        throw "$Label DLL ownership cleanup failed with exit code $pruneExitCode."
    }
}

function Reset-ProjectLinkerIntermediate {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $projectDirectory = Split-Path -Parent $ProjectPath
    $linkDirectory = Join-Path $projectDirectory "obj\$Configuration\net10.0-windows\$RuntimeIdentifier\linked"
    if (Test-Path -LiteralPath $linkDirectory) {
        Remove-Item -LiteralPath $linkDirectory -Recurse -Force
    }
}

function Publish-IsolatedStages(
    [string]$sharedCopyUsedAssemblyListFile,
    [string]$sharedCopyAssemblyListFile,
    [string]$passName) {
    $publishArguments = @($commonPublishArguments)
    if (-not [string]::IsNullOrWhiteSpace($sharedCopyUsedAssemblyListFile)) {
        if (-not (Test-Path -LiteralPath $sharedCopyUsedAssemblyListFile -PathType Leaf)) {
            throw "The ILLink copyused assembly-list file does not exist: $sharedCopyUsedAssemblyListFile"
        }

        # Do not put comma- or semicolon-delimited assembly lists into MSBuild
        # -p: switches. Pass only file paths and let the imported target read the
        # one-name-per-line lists.
        $publishArguments += "-p:SharedCopyUsedAssemblyListFile=$sharedCopyUsedAssemblyListFile"
    }
    if (-not [string]::IsNullOrWhiteSpace($sharedCopyAssemblyListFile)) {
        if (-not (Test-Path -LiteralPath $sharedCopyAssemblyListFile -PathType Leaf)) {
            throw "The ILLink copy assembly-list file does not exist: $sharedCopyAssemblyListFile"
        }

        $publishArguments += "-p:SharedCopyAssemblyListFile=$sharedCopyAssemblyListFile"
    }

    # Do not pass IntermediateLinkDir on the command line. MSBuild command-line
    # properties are global and flow into project references; the frontend has a
    # non-runtime reference to the Agent. Sharing one linked-output directory and
    # Link.semaphore can make one project reuse another project's linker output.
    # Keep the SDK's project-local default and delete it before every publish so
    # changes to direct ILLink actions can never be skipped incrementally.
    Reset-ProjectLinkerIntermediate -ProjectPath $frontendProject
    Reset-ProjectLinkerIntermediate -ProjectPath $agentProject

    Write-Host 'Publishing isolated trimmed Windows frontend staging output...'
    & dotnet publish $frontendProject @publishArguments --output $frontendStage
    if ($LASTEXITCODE -ne 0) { throw "Frontend publish failed with exit code $LASTEXITCODE." }
    Remove-StagedUnownedDlls -Label 'Frontend' -Directory $frontendStage `
        -ManifestFileName 'PasswordManagerLocal.deps.json' -PassName $passName
    Assert-StagedFiles -Label 'Frontend' -Directory $frontendStage `
        -FileNames $frontendStageRequiredFiles -Moment "after the $passName frontend publish"

    # The frontend build may build the Agent through its non-runtime project
    # reference. Clear the Agent linker output again before its authoritative
    # standalone publication.
    Reset-ProjectLinkerIntermediate -ProjectPath $agentProject

    Write-Host 'Publishing isolated trimmed Windows Agent staging output...'
    & dotnet publish $agentProject @publishArguments --output $agentStage
    if ($LASTEXITCODE -ne 0) { throw "Agent publish failed with exit code $LASTEXITCODE." }
    Remove-StagedUnownedDlls -Label 'Agent' -Directory $agentStage `
        -ManifestFileName 'PasswordManagerLocal.Windows.Agent.deps.json' -PassName $passName
    Assert-StagedFiles -Label 'Agent' -Directory $agentStage `
        -FileNames $agentStageRequiredFiles -Moment "after the $passName Agent publish"
    Assert-StagedFiles -Label 'Frontend' -Directory $frontendStage `
        -FileNames $frontendStageRequiredFiles -Moment "after the $passName Agent publish"
}

function Analyze-StagedCollisions {
    if (Test-Path -LiteralPath $collisionReportPath) { Remove-Item -LiteralPath $collisionReportPath -Force }
    if (Test-Path -LiteralPath $copyUsedAssemblyListPath) { Remove-Item -LiteralPath $copyUsedAssemblyListPath -Force }

    $analysisArguments = @(
        'run', '--project', $packagingProject,
        '--configuration', $Configuration,
        '--', 'analyze-collisions',
        '--frontend', $frontendStage,
        '--agent', $agentStage,
        '--report', $collisionReportPath,
        '--assembly-list', $copyUsedAssemblyListPath
    )

    $analysisOutput = & dotnet @analysisArguments
    $analysisExitCode = $LASTEXITCODE
    $analysisOutput | ForEach-Object { Write-Host $_ }
    if ($analysisExitCode -ne 0) { throw "Staged collision analysis failed with exit code $analysisExitCode." }

    if (-not (Test-Path -LiteralPath $copyUsedAssemblyListPath -PathType Leaf)) {
        throw "Collision analysis did not create its assembly list: $copyUsedAssemblyListPath"
    }

    return @(
        Get-Content -LiteralPath $copyUsedAssemblyListPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
}

function Write-AssemblyListFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$AssemblyNames
    )

    $normalized = @(
        $AssemblyNames |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllLines(
        [System.IO.Path]::GetFullPath($Path),
        [string[]]$normalized,
        [System.Text.UTF8Encoding]::new($false))
}

function New-DeterministicProductArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    # The built-in PowerShell archive cmdlet copies source LastWriteTime values into ZIP entries. Some
    # SDK/runtime-pack files can carry timestamps outside ZIP's DOS timestamp
    # range (1980-01-01 through 2107-12-31), which makes ZipArchiveEntry reject
    # the value. Build the archive directly and use one stable, valid timestamp
    # for every entry instead of mutating the published product files.
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop

    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory)
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Archive source directory is missing: $sourceRoot"
    }

    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    $sourcePrefix = $sourceRoot
    if (-not $sourcePrefix.EndsWith($separator, [StringComparison]::Ordinal)) {
        $sourcePrefix += $separator
    }

    $sourceFiles = @(
        Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
            Sort-Object FullName
    )
    if ($sourceFiles.Count -eq 0) {
        throw "Archive source directory contains no files: $sourceRoot"
    }

    $destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)
    $destinationDirectory = Split-Path -Parent $destinationFullPath
    if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }
    if (Test-Path -LiteralPath $destinationFullPath) {
        Remove-Item -LiteralPath $destinationFullPath -Force
    }

    # 2000-01-01 is intentionally inside ZIP's representable DOS range and has
    # exact two-second precision, so no implementation-specific rounding occurs.
    $fixedZipTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $expectedEntryNames = [System.Collections.Generic.List[string]]::new()

    $zipStream = $null
    $zipArchive = $null
    try {
        $zipStream = [System.IO.File]::Open(
            $destinationFullPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $zipArchive = [System.IO.Compression.ZipArchive]::new(
            $zipStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)

        foreach ($file in $sourceFiles) {
            $fileFullPath = [System.IO.Path]::GetFullPath($file.FullName)
            if (-not $fileFullPath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive source escaped the product root: $fileFullPath"
            }

            $entryName = $fileFullPath.Substring($sourcePrefix.Length).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName)) {
                throw "Cannot derive a ZIP entry name for: $fileFullPath"
            }
            $expectedEntryNames.Add($entryName)

            $entry = $zipArchive.CreateEntry(
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedZipTimestamp

            $input = $null
            $output = $null
            try {
                $input = [System.IO.File]::Open(
                    $fileFullPath,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read,
                    [System.IO.FileShare]::Read)
                $output = $entry.Open()
                $input.CopyTo($output)
            } finally {
                if ($null -ne $output) { $output.Dispose() }
                if ($null -ne $input) { $input.Dispose() }
            }
        }
    } finally {
        if ($null -ne $zipArchive) { $zipArchive.Dispose() }
        if ($null -ne $zipStream) { $zipStream.Dispose() }
    }

    if (-not (Test-Path -LiteralPath $destinationFullPath -PathType Leaf)) {
        throw "Product archive was not created: $destinationFullPath"
    }

    # Reopen and validate the archive before reporting success. This catches
    # missing/duplicate entries and any future invalid timestamp regression.
    $readStream = $null
    $readArchive = $null
    try {
        $readStream = [System.IO.File]::Open(
            $destinationFullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $readArchive = [System.IO.Compression.ZipArchive]::new(
            $readStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)

        $actualEntries = @($readArchive.Entries | Sort-Object FullName)
        $expectedNames = @($expectedEntryNames | Sort-Object)
        if ($actualEntries.Count -ne $expectedNames.Count) {
            throw "Product archive entry count mismatch. Expected $($expectedNames.Count), actual $($actualEntries.Count)."
        }

        for ($index = 0; $index -lt $expectedNames.Count; $index++) {
            if (-not [string]::Equals(
                    $actualEntries[$index].FullName,
                    $expectedNames[$index],
                    [StringComparison]::Ordinal)) {
                throw "Product archive entry mismatch at index $index. Expected '$($expectedNames[$index])', actual '$($actualEntries[$index].FullName)'."
            }

            $entryTimestamp = $actualEntries[$index].LastWriteTime
            if ($entryTimestamp.Year -lt 1980 -or $entryTimestamp.Year -gt 2107) {
                throw "Product archive contains an invalid ZIP timestamp: $($actualEntries[$index].FullName) -> $entryTimestamp"
            }
        }
    } finally {
        if ($null -ne $readArchive) { $readArchive.Dispose() }
        if ($null -ne $readStream) { $readStream.Dispose() }
    }

    Write-Host "Verified deterministic ZIP archive: $($expectedEntryNames.Count) entries; fixed timestamp $($fixedZipTimestamp.ToString('O'))."
}

Reset-StagingDirectories
Publish-IsolatedStages '' '' 'initial'
$copyUsedAssemblies = @(Analyze-StagedCollisions)

if ($copyUsedAssemblies.Count -gt 0) {
    Copy-Item -LiteralPath $collisionReportPath -Destination (Join-Path $reportRoot 'staged-collision-analysis.initial.json') -Force
    Copy-Item -LiteralPath $copyUsedAssemblyListPath -Destination $initialCopyUsedAssemblyListPath -Force
    Write-Host ''
    Write-Host "Republishing both isolated stages with narrow ILLink copyused actions for: $($copyUsedAssemblies -join ', ')"
    Reset-StagingDirectories
    Publish-IsolatedStages $initialCopyUsedAssemblyListPath '' 'copyused'

    $remainingAssemblies = @(Analyze-StagedCollisions)
    if ($remainingAssemblies.Count -gt 0) {
        Copy-Item -LiteralPath $collisionReportPath -Destination (Join-Path $reportRoot 'staged-collision-analysis.copyused.json') -Force

        # ILLink can internally promote CopyUsed to Save when it must rewrite
        # assembly references or type-forwarder scopes after removing other
        # assemblies. Save rewrites the assembly, so two independent trim graphs
        # may still produce different bytes. Promote only that residual set to
        # the stronger Copy action, which copies the original input bytes while
        # still analyzing dependencies.
        $copyAssemblies = @($remainingAssemblies)
        $copyPass = 1

        while ($true) {
            Write-AssemblyListFile -Path $copyAssemblyListPath -AssemblyNames $copyAssemblies
            Write-Host ''
            Write-Host "Residual collisions remain after copyused; republishing with ILLink copy for: $($copyAssemblies -join ', ')"
            Reset-StagingDirectories
            Publish-IsolatedStages $initialCopyUsedAssemblyListPath $copyAssemblyListPath "copy harmonization pass $copyPass"

            $remainingAssemblies = @(Analyze-StagedCollisions)
            if ($remainingAssemblies.Count -eq 0) {
                break
            }

            Copy-Item -LiteralPath $collisionReportPath `
                -Destination (Join-Path $reportRoot "staged-collision-analysis.copy-pass-$copyPass.json") -Force

            $newCopyAssemblies = @(
                $remainingAssemblies |
                    Where-Object { $copyAssemblies -notcontains $_ } |
                    Sort-Object -Unique
            )
            if ($newCopyAssemblies.Count -eq 0) {
                throw "Same-path managed collisions remain even after ILLink copy harmonization: $($remainingAssemblies -join ', ')"
            }

            $copyAssemblies = @(
                @($copyAssemblies) + @($newCopyAssemblies) |
                    Sort-Object -Unique
            )
            $copyPass++
        }
    }
}

$mergeArguments = @(
    'run', '--project', $packagingProject,
    '--configuration', $Configuration,
    '--', 'merge',
    '--frontend', $frontendStage,
    '--agent', $agentStage,
    '--output', $productRoot,
    '--report', $reportRoot,
    '--runtime-identifier', $RuntimeIdentifier,
    '--target-framework', 'net10.0-windows'
)
if (Test-Path -LiteralPath $BaselineTreePath -PathType Leaf) {
    $mergeArguments += @('--baseline-tree', [System.IO.Path]::GetFullPath($BaselineTreePath))
}
if ($null -ne $BaselineTotalBytes) {
    $mergeArguments += @('--baseline-total-bytes', ([string]$BaselineTotalBytes.Value))
}

Write-Host 'Validating isolated outputs, hashing, merging, and deduplicating...'
& dotnet @mergeArguments
if ($LASTEXITCODE -ne 0) { throw "Windows packaging failed with exit code $LASTEXITCODE." }

& $verificationScript -PublishDirectory $productRoot -RuntimeIdentifier $RuntimeIdentifier -RepositoryRoot $repositoryRoot

if ($RunSmokeTests) {
    if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        throw 'Process smoke tests require Windows.'
    }
    foreach ($executableName in @('PasswordManagerLocal.Windows.Agent.exe', 'PasswordManagerLocal.exe')) {
        $executablePath = Join-Path $productRoot $executableName
        $process = Start-Process -FilePath $executablePath -WorkingDirectory $productRoot -PassThru
        try {
            Start-Sleep -Milliseconds 1500
            if ($process.HasExited -and $process.ExitCode -ne 0) {
                throw "$executableName exited during smoke validation with code $($process.ExitCode)."
            }
        } finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
    }
}

New-DeterministicProductArchive -SourceDirectory $productRoot -DestinationPath $zipPath

Write-Host ''
Write-Host "Product directory: $productRoot"
Write-Host "Packaging reports: $reportRoot"
Write-Host "Product archive: $zipPath"
