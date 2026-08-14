[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$DependencyManifestFileName,

    [switch]$RemoveAvaloniaDesignerAssets,

    [switch]$RemoveUnusedXmlSerializerAssembly
)

$ErrorActionPreference = 'Stop'

function Remove-JsonProperty {
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -ne $Object) { [void]$Object.PSObject.Properties.Remove($Name) }
}

function Get-JsonPropertyValue {
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][scriptblock]$Predicate)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { & $Predicate $_.Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Remove-JsonPropertiesMatching {
    param([AllowNull()][object]$Object, [Parameter(Mandatory = $true)][scriptblock]$Predicate)
    if ($null -eq $Object) { return }
    $names = @($Object.PSObject.Properties | Where-Object { & $Predicate $_.Name } | ForEach-Object { $_.Name })
    foreach ($name in $names) { Remove-JsonProperty -Object $Object -Name $name }
}

function Remove-PublishedFiles {
    param([Parameter(Mandatory = $true)][string]$PublishDirectory)

    $exactFiles = @(
        'createdump.exe',
        'mscordaccore.dll',
        'mscordbi.dll',
        'Microsoft.DiaSymReader.Native.amd64.dll'
    )
    if ($RemoveAvaloniaDesignerAssets) {
        $exactFiles += @('Avalonia.DesignerSupport.dll', 'Avalonia.Remote.Protocol.dll')
    }
    if ($RemoveUnusedXmlSerializerAssembly) {
        # A residual ILLink 'copy' action can retain this facade even though the
        # normal trimmed Agent graph proves that no reachable Agent code needs it.
        # Keep the Phase 6 minimal-Agent boundary authoritative after every pass.
        $exactFiles += 'System.Xml.XmlSerializer.dll'
    }

    foreach ($fileName in $exactFiles) {
        $path = Join-Path $PublishDirectory $fileName
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }

    Get-ChildItem -LiteralPath $PublishDirectory -File -Filter 'mscordaccore_*.dll' -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function Update-DependencyManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$ManifestFileName
    )

    $depsPath = Join-Path $PublishDirectory $ManifestFileName
    if (-not (Test-Path -LiteralPath $depsPath)) { throw "Dependency manifest was not found: $depsPath" }

    $dependencyContext = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $runtimeTarget = Get-JsonPropertyValue -Object $dependencyContext.targets -Predicate {
        param($name)
        $name -like '*/win-x64'
    }
    if ($null -eq $runtimeTarget) { throw "The win-x64 runtime target could not be found in '$depsPath'." }

    if ($RemoveAvaloniaDesignerAssets) {
        foreach ($runtimeLibrary in @($runtimeTarget.PSObject.Properties)) {
            Remove-JsonPropertiesMatching -Object $runtimeLibrary.Value.runtime -Predicate {
                param($name)
                $name -like '*Avalonia.DesignerSupport.dll'
            }
            Remove-JsonProperty -Object $runtimeLibrary.Value.dependencies -Name 'Avalonia.Remote.Protocol'
        }
        Remove-JsonPropertiesMatching -Object $runtimeTarget -Predicate {
            param($name)
            $name -like 'Avalonia.Remote.Protocol/*'
        }
        Remove-JsonPropertiesMatching -Object $dependencyContext.libraries -Predicate {
            param($name)
            $name -like 'Avalonia.Remote.Protocol/*'
        }
    }

    if ($RemoveUnusedXmlSerializerAssembly) {
        foreach ($runtimeLibrary in @($runtimeTarget.PSObject.Properties)) {
            foreach ($assetCollectionName in @('runtime', 'runtimeTargets')) {
                $assetCollection = Get-JsonPropertyValue -Object $runtimeLibrary.Value -Predicate {
                    param($name)
                    $name -eq $assetCollectionName
                }
                Remove-JsonPropertiesMatching -Object $assetCollection -Predicate {
                    param($name)
                    $normalizedName = $name.Replace('\', '/')
                    $normalizedName -eq 'System.Xml.XmlSerializer.dll' -or
                        $normalizedName.EndsWith('/System.Xml.XmlSerializer.dll', [System.StringComparison]::OrdinalIgnoreCase)
                }
            }
            Remove-JsonProperty -Object $runtimeLibrary.Value.dependencies -Name 'System.Xml.XmlSerializer'
        }
        Remove-JsonPropertiesMatching -Object $runtimeTarget -Predicate {
            param($name)
            $name -like 'System.Xml.XmlSerializer/*'
        }
        Remove-JsonPropertiesMatching -Object $dependencyContext.libraries -Predicate {
            param($name)
            $name -like 'System.Xml.XmlSerializer/*'
        }
    }

    $runtimePack = Get-JsonPropertyValue -Object $runtimeTarget -Predicate {
        param($name)
        $name -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*'
    }
    if ($null -eq $runtimePack) { throw "The self-contained win-x64 runtime pack entry could not be found in '$depsPath'." }

    foreach ($fileName in @('Microsoft.DiaSymReader.Native.amd64.dll', 'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll')) {
        Remove-JsonProperty -Object $runtimePack.native -Name $fileName
    }
    Remove-JsonPropertiesMatching -Object $runtimePack.native -Predicate {
        param($name)
        $name -like 'mscordaccore_*.dll'
    }

    $json = $dependencyContext | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($depsPath, $json, [System.Text.UTF8Encoding]::new($false))
}

$cleanOutputPath = $OutputPath.Trim().Trim([char]34)
$cleanOutputPath = $cleanOutputPath.TrimEnd([char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
))
if ([string]::IsNullOrWhiteSpace($cleanOutputPath)) { throw 'The publish directory argument was empty.' }

$resolvedOutputPath = [System.IO.Path]::GetFullPath($cleanOutputPath)
if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Container)) {
    throw "Publish directory does not exist: $resolvedOutputPath"
}

Remove-PublishedFiles -PublishDirectory $resolvedOutputPath
Update-DependencyManifest -PublishDirectory $resolvedOutputPath -ManifestFileName $DependencyManifestFileName

if ($RemoveAvaloniaDesignerAssets) {
    Write-Host 'Removed Avalonia designer/remote protocol assets from the frontend publish.'
}
if ($RemoveUnusedXmlSerializerAssembly) {
    Write-Host 'Removed unused System.Xml.XmlSerializer facade from the Agent publish and dependency manifest.'
}
Write-Host 'Removed unused CoreCLR crash-dump and managed-debugger assets from the publish.'
