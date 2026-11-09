#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EnglishMsi,
    [Parameter(Mandatory = $true)][string]$HungarianMsi,
    [Parameter(Mandatory = $true)][string]$TransformPath
)

$ErrorActionPreference = 'Stop'

function Invoke-MsiMethod($Object, [string]$Name, [object[]]$Arguments = @()) {
    $Object.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Object, $Arguments)
}

function Release-MsiObject($Object) {
    if ($null -ne $Object) {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($Object)
    }
}

function Get-MsiText($Database, [string]$Query) {
    $view = $null
    $record = $null
    try {
        $view = Invoke-MsiMethod $Database 'OpenView' @($Query)
        [void](Invoke-MsiMethod $view 'Execute' @($null))
        $record = Invoke-MsiMethod $view 'Fetch'
        if ($null -eq $record) {
            throw "The MSI query returned no row: $Query"
        }
        $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty,
            $null, $record, @(1))
    }
    finally {
        Release-MsiObject $record
        Release-MsiObject $view
    }
}

$languageQuery = 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductLanguage'''
$headingQuery = 'SELECT `Text` FROM `Control` WHERE `Dialog_` = ''PmlSetupFinished'' AND `Control` = ''Heading'''
$readOnly = 0
$transact = 1
$insert = 1
$transformErrorNone = 0
$validateTransformProduct = 2
$templateSummaryProperty = 7

$installer = $null
$english = $null
$hungarian = $null
$view = $null
$record = $null
$summary = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $english = Invoke-MsiMethod $installer 'OpenDatabase' @($EnglishMsi, $transact)
    $hungarian = Invoke-MsiMethod $installer 'OpenDatabase' @($HungarianMsi, $readOnly)

    if ((Get-MsiText $english $languageQuery) -ne '1033' -or (Get-MsiText $hungarian $languageQuery) -ne '1038') {
        throw 'WiX must produce an English MSI (1033) and a Hungarian MSI (1038).'
    }
    foreach ($property in @('ProductCode', 'UpgradeCode', 'ProductVersion')) {
        $query = 'SELECT `Value` FROM `Property` WHERE `Property` = ''{0}''' -f $property
        if ((Get-MsiText $english $query) -cne (Get-MsiText $hungarian $query)) {
            throw "The installer language builds have different $property values."
        }
    }

    $englishHeading = Get-MsiText $english $headingQuery
    $hungarianHeading = Get-MsiText $hungarian $headingQuery
    if ($englishHeading -ceq $hungarianHeading) {
        throw 'The Hungarian MSI is missing the localized finish dialog.'
    }
    if (-not (Invoke-MsiMethod $hungarian 'GenerateTransform' @($english, $TransformPath))) {
        throw 'Windows Installer could not generate a Hungarian transform.'
    }
    [void](Invoke-MsiMethod $hungarian 'CreateTransformSummaryInfo' @(
        $english, $TransformPath, $transformErrorNone, $validateTransformProduct))
    Release-MsiObject $hungarian
    $hungarian = $null

    # Apply the transform without committing to verify it against the actual English database.
    [void](Invoke-MsiMethod $english 'ApplyTransform' @($TransformPath, $transformErrorNone))
    if ((Get-MsiText $english $languageQuery) -ne '1038' -or (Get-MsiText $english $headingQuery) -cne $hungarianHeading) {
        throw 'The Hungarian transform did not localize the MSI.'
    }
    Release-MsiObject $english
    $english = $null

    $english = Invoke-MsiMethod $installer 'OpenDatabase' @($EnglishMsi, $transact)
    if ((Get-MsiText $english $languageQuery) -ne '1033' -or (Get-MsiText $english $headingQuery) -cne $englishHeading) {
        throw 'The base MSI must remain in English before embedding the Hungarian transform.'
    }
    $view = Invoke-MsiMethod $english 'OpenView' @('SELECT `Name`, `Data` FROM `_Storages`')
    [void](Invoke-MsiMethod $view 'Execute' @($null))
    $record = Invoke-MsiMethod $installer 'CreateRecord' @(2)
    [void]$record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::SetProperty, $null, $record, @(1, '1038'))
    [void](Invoke-MsiMethod $record 'SetStream' @(2, $TransformPath))
    [void](Invoke-MsiMethod $view 'Modify' @($insert, $record))
    [void](Invoke-MsiMethod $view 'Close')

    # List both package languages, with the base language first.
    $summary = $english.GetType().InvokeMember('SummaryInformation', [Reflection.BindingFlags]::GetProperty,
        $null, $english, @(1))
    [void]$summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty,
        $null, $summary, @($templateSummaryProperty, 'x64;1033,1038'))
    [void](Invoke-MsiMethod $summary 'Persist')
    [void](Invoke-MsiMethod $english 'Commit')
    if ((Get-MsiText $english 'SELECT `Name` FROM `_Storages` WHERE `Name` = ''1038''') -ne '1038') {
        throw 'The Hungarian transform is missing from the final MSI.'
    }
}
finally {
    Release-MsiObject $summary
    Release-MsiObject $record
    Release-MsiObject $view
    Release-MsiObject $hungarian
    Release-MsiObject $english
    Release-MsiObject $installer
}
