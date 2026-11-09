#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SvgPath,
    [Parameter(Mandatory = $true)][string]$IconPath
)

$ErrorActionPreference = 'Stop'

function Find-Inkscape {
    if ($env:PML_INKSCAPE_PATH) {
        if (-not (Test-Path -LiteralPath $env:PML_INKSCAPE_PATH -PathType Leaf)) {
            throw "PML_INKSCAPE_PATH does not point to an executable: $env:PML_INKSCAPE_PATH"
        }
        return (Resolve-Path -LiteralPath $env:PML_INKSCAPE_PATH).ProviderPath
    }

    $command = Get-Command -Name 'inkscape.com', 'inkscape', 'inkscape.exe' -CommandType Application `
        -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    foreach ($directory in @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $directory) { continue }
        foreach ($name in @('inkscape.com', 'inkscape.exe')) {
            $candidate = Join-Path $directory "Inkscape\bin\$name"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }
    throw 'Install Inkscape 1.4 or newer to generate the Windows icon. Add it to PATH or set PML_INKSCAPE_PATH to its executable. See Docs\WINDOWS_INSTALLER.md.'
}

$svg = (Resolve-Path -LiteralPath $SvgPath).ProviderPath
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($IconPath)
$destinationDirectory = [IO.Path]::GetDirectoryName($destination)
[void][IO.Directory]::CreateDirectory($destinationDirectory)

# The application, Agent, and installer can build in separate processes.
$lock = $null
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($null -eq $lock) {
    try {
        $lock = [IO.File]::Open("$destination.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        if ($timer.Elapsed.TotalSeconds -ge 30) { throw }
        Start-Sleep -Milliseconds 100
    }
}

$workDirectory = $null
try {
    # Another build may have generated the icon while we waited for the lock.
    $iconTime = [IO.File]::GetLastWriteTimeUtc($destination)
    if ([IO.File]::Exists($destination) -and
        $iconTime -ge [IO.File]::GetLastWriteTimeUtc($svg) -and
        $iconTime -ge [IO.File]::GetLastWriteTimeUtc($PSCommandPath)) {
        return
    }

    $inkscape = Find-Inkscape
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $workDirectory = Join-Path $destinationDirectory ('.icon-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($workDirectory)
    [IO.File]::Copy($svg, (Join-Path $workDirectory 'icon.svg'))

    # One renderer process, with a separate vector render for each size.
    # Simple relative names also keep source paths out of the action syntax.
    $actions = @('export-area-page', 'export-type:png', 'export-png-color-mode:RGBA_8') + @(
        foreach ($size in $sizes) {
            "export-width:$size"
            "export-height:$size"
            "export-filename:$size.png"
            'export-do'
        }
    )
    Push-Location -LiteralPath $workDirectory
    try {
        & $inkscape 'icon.svg' "--actions=$($actions -join ';')" | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Inkscape failed to render the Windows icon with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $frames = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $frame = [IO.File]::ReadAllBytes((Join-Path $workDirectory "$size.png"))
        if ($frame.Length -lt 33 -or [BitConverter]::ToString($frame, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A' -or
            [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($frame, 16)) -ne $size -or
            [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($frame, 20)) -ne $size -or
            $frame[24] -ne 8 -or $frame[25] -ne 6) {
            throw "Inkscape did not produce a valid ${size}x${size} RGBA PNG."
        }
        $frames.Add($frame)
    }

    $pendingIcon = Join-Path $workDirectory 'icon.ico'
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($pendingIcon))
    try {
        # ICONDIR followed by ICONDIRENTRY records and their PNG payloads.
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = [byte]($sizes[$index] % 256) # An ICO dimension of zero represents 256 pixels.
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frames[$index].Length)
            $writer.Write([UInt32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) {
            $writer.Write($frame)
        }
    }
    finally {
        $writer.Dispose()
    }

    if ([IO.File]::Exists($destination)) {
        [IO.File]::Replace($pendingIcon, $destination, (Join-Path $workDirectory 'previous.ico'))
    }
    else {
        [IO.File]::Move($pendingIcon, $destination)
    }
    Write-Host "Generated Windows icon: $destination"
}
finally {
    $lock.Dispose()
    if ($workDirectory -and [IO.Directory]::Exists($workDirectory)) {
        [IO.Directory]::Delete($workDirectory, $true)
    }
}
