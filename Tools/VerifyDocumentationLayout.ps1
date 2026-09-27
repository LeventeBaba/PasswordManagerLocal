[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$documentationRoot = Join-Path $repositoryRoot 'Docs'

$requiredDocuments = @(
    'README.md',
    'PHASE11_ANDROID_INTEGRATION_PLATFORM_HARDENING_STATIC_VERIFICATION.md',
    'PHASE11_IMPLEMENTATION_CHECKS.md',
    'PHASE11_MODIFIED_FILES.md',
    'PHASE11_ANDROID_AUTOMATED_TEST_MATRIX.md',
    'PHASE11_ANDROID_EMULATOR_DEVICE_TEST_MATRIX.md',
    'PHASE11_ANDROID_SECURITY_REVIEW.md',
    'PHASE11_FINAL_ARCHITECTURE_AND_ACCEPTANCE.md'
)

if (-not (Test-Path -LiteralPath $documentationRoot -PathType Container)) {
    throw "Documentation directory not found: $documentationRoot"
}

$missingDocuments = @($requiredDocuments | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $documentationRoot $_) -PathType Leaf)
})
if ($missingDocuments.Count -gt 0) {
    throw "Required documentation is missing: $($missingDocuments -join ', ')"
}

$excludedDirectoryNames = @('.git', '.vs', 'bin', 'obj', 'artifacts')
$markdownFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.md' | Where-Object {
    $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
    $segments = @($relativePath -split '[\\/]')
    -not ($segments | Where-Object { $excludedDirectoryNames -contains $_ })
})

$outsideDocumentation = @($markdownFiles | Where-Object {
    $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
    $relativePath -ne 'README.md' -and
        -not $relativePath.StartsWith("Docs$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase) -and
        -not $relativePath.StartsWith('Docs/', [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object {
    [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
})

if ($outsideDocumentation.Count -gt 0) {
    throw "Markdown files must be under Docs (except an optional root README.md): $($outsideDocumentation -join ', ')"
}

$duplicateNames = @($markdownFiles |
    Group-Object Name |
    Where-Object Count -gt 1 |
    ForEach-Object Name)
if ($duplicateNames.Count -gt 0) {
    Write-Warning "Duplicate Markdown filenames were found in different directories: $($duplicateNames -join ', ')"
}

Write-Host "Documentation layout is valid. Required=$($requiredDocuments.Count); Markdown=$($markdownFiles.Count)."
