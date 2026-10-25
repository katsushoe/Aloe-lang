[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MsiPath,

    [string] $ExpectedVersion = '0.1.0',
    [string] $ExpectedProductCode = '{0FDC5E40-27FB-4891-997D-255CA29F47E6}',
    [string] $ExpectedUpgradeCode = '{60A2445F-3183-4842-9170-3C9E72A75C93}'
)

$ErrorActionPreference = 'Stop'
$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aloe-msi-check-' + [Guid]::NewGuid().ToString('N'))
$decompiledPath = Join-Path $tempRoot 'package.wxs'
$namespace = [Xml.XmlNamespaceManager]::new((New-Object Xml.XmlDocument).NameTable)
$namespace.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
$checks = [Collections.Generic.List[string]]::new()

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    & wix msi decompile $resolvedMsiPath -o $decompiledPath
    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI decompilation failed with exit code $LASTEXITCODE."
    }

    $document = [Xml.XmlDocument]::new()
    $document.Load($decompiledPath)
    $package = $document.SelectSingleNode('/w:Wix/w:Package', $namespace)
    if ($null -eq $package) { throw 'The MSI has no decompiled Package element.' }

    foreach ($assertion in @(
        @{ Name = 'ProductVersion'; Actual = $package.GetAttribute('Version'); Expected = $ExpectedVersion },
        @{ Name = 'ProductCode'; Actual = $package.GetAttribute('ProductCode'); Expected = $ExpectedProductCode },
        @{ Name = 'UpgradeCode'; Actual = $package.GetAttribute('UpgradeCode'); Expected = $ExpectedUpgradeCode },
        @{ Name = 'Manufacturer'; Actual = $package.GetAttribute('Manufacturer'); Expected = 'Akatsukisoft' }
    )) {
        if ($assertion.Actual -ne $assertion.Expected) {
            throw "$($assertion.Name) mismatch: expected '$($assertion.Expected)', found '$($assertion.Actual)'."
        }
        $checks.Add($assertion.Name)
    }

    if ($null -eq $document.SelectSingleNode('//w:StandardDirectory[@Id="ProgramFiles64Folder"]', $namespace)) {
        throw 'The MSI is not rooted in the 64-bit Program Files directory.'
    }
    $packageSource = Join-Path $PSScriptRoot 'Package.wxs'
    $packageSourceText = [IO.File]::ReadAllText($packageSource)
    if ($packageSourceText -notmatch '(?s)<Package\b[^>]*\bScope="perMachine"') {
        throw 'The WiX package source does not explicitly set all-user perMachine scope.'
    }
    $checks.Add('all-user scope in WiX source and 64-bit MSI install root')

    $fileNames = @($document.SelectNodes('//w:File/@Name', $namespace) | ForEach-Object { $_.Value })
    foreach ($hostName in @('aloec.exe', 'aloevm.exe', 'aloe2wasm.exe', 'aloe-repl.exe', 'AloeSample.exe')) {
        if ($fileNames -notcontains $hostName) { throw "The MSI payload is missing $hostName." }
    }
    $checks.Add('five desktop host executables')
    $browserPayload = @($fileNames | Where-Object {
        $_ -match '^(AloeRepl\.Wasm(\.(dll|deps\.json|runtimeconfig\.json))?|blazor\.boot\.json|index\.html|dotnet\.js|dotnet\.native\.wasm|dotnet\.wasm)$'
    })
    if ($browserPayload.Count -gt 0) {
        throw 'The MSI payload unexpectedly contains the excluded Browser REPL.'
    }
    $checks.Add('Browser REPL excluded')
    if ($fileNames | Where-Object { $_ -match '\.pdb$' }) { throw 'The MSI payload unexpectedly contains debug symbol files.' }
    $checks.Add('no debug symbol files')

    foreach ($configName in @('aloec.json', 'aloevm.json', 'aloe2wasm.json', 'LICENSE')) {
        if ($fileNames -notcontains $configName) { throw "The MSI payload is missing $configName." }
    }
    $checks.Add('configuration and license payload')

    foreach ($directoryId in @('LOGSFOLDER', 'DATAFOLDER')) {
        $directory = $document.SelectSingleNode("//w:Directory[@Id='$directoryId']", $namespace)
        $permission = if ($null -eq $directory) { $null } else { $directory.SelectSingleNode('.//w:PermissionEx[contains(@Sddl,";;;BU")]', $namespace) }
        if ($null -eq $permission -or -not $permission.GetAttribute('Sddl').Contains('0x1301bf;;;BU')) {
            throw "The $directoryId directory has no expected Users Modify ACL entry."
        }
    }
    $binaryDirectory = $document.SelectSingleNode('//w:Directory[@Id="BINFOLDER"]', $namespace)
    if ($null -eq $binaryDirectory -or $null -ne $binaryDirectory.SelectSingleNode('.//w:PermissionEx[contains(@Sddl,";;;BU")]', $namespace)) {
        throw 'The binary directory is missing or grants an explicit Users ACL.'
    }
    $checks.Add('logs/data Users ACL entries')

    $environment = $document.SelectSingleNode('//w:Environment[@Name="ALOE_INSTALL_ROOT"]', $namespace)
    if ($null -eq $environment -or $environment.GetAttribute('Action') -ne 'create' -or
        $environment.GetAttribute('System') -ne 'yes' -or $environment.GetAttribute('Value') -ne '[INSTALLFOLDER]') {
        throw 'The machine ALOE_INSTALL_ROOT environment entry is missing or has an unexpected value.'
    }
    $checks.Add('machine ALOE_INSTALL_ROOT')

    foreach ($propertyId in @('ALOE_MACHINE_INSTALL_ROOT', 'ALOE_OWNED_INSTALL_ROOT')) {
        if ($null -eq $document.SelectSingleNode("//w:Property[@Id='$propertyId']/w:RegistrySearch", $namespace)) {
            throw "The MSI is missing the $propertyId registry search."
        }
    }
    $checks.Add('existing machine install-root searches')

    foreach ($check in $checks) { Write-Output "PASS $check" }
    Write-Output "Validated $($checks.Count) MSI artifact checks: $resolvedMsiPath"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
