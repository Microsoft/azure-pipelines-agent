[CmdletBinding()]
param()

$rootsKeyName = 'Software\Microsoft\Windows Kits\Installed Roots'
$valueNames = Get-RegistryValueNames -Hive 'LocalMachine' -View 'Registry32' -KeyName $rootsKeyName
$versionInfos = @( )
foreach ($valueName in $valueNames) {
    if (!"$valueName".StartsWith('KitsRoot', 'OrdinalIgnoreCase')) {
        continue
    }

    $installDirectory = Get-RegistryValue -Hive 'LocalMachine' -View 'Registry32' -KeyName $rootsKeyName -ValueName $valueName
    $splitInstallDirectory =
        "$installDirectory".Split(@( ([System.IO.Path]::DirectorySeparatorChar) ) ) |
        ForEach-Object { "$_".Trim() } |
        Where-Object { $_ }
    $splitInstallDirectory = @( $splitInstallDirectory )
    if ($splitInstallDirectory.Length -eq 0) {
        continue
    }
	
	# Format input version to support Windows "10" versioning (parsing needs major.minor[.build[.revision]] format)
	$inputVersion = $splitInstallDirectory[-1]
	if ($inputVersion -notcontains ".") {
		$inputVersion += ".0"
	}

    $version = $null
    if (!([System.Version]::TryParse($inputVersion, [ref]$version))) {
        continue
    }

    Write-Capability -Name "WindowsKit_$($version.Major).$($version.Minor)" -Value $installDirectory
    $versionInfos += @{
        Version = $version
        InstallDirectory = $installDirectory
    }
}

# Add a capability for the max Windows Kit.
if ($versionInfos.Length) {
    $maxInfo =
        $versionInfos |
        Sort-Object -Descending -Property Version |
        Select-Object -First 1
    Write-Capability -Name "WindowsKit" -Value $maxInfo.InstallDirectory
}

# Detect installed versions of Windows 10 Kit.
$windowsUAPSdks = @( )
$versionSubKeyNames =
    Get-RegistrySubKeyNames -Hive 'LocalMachine' -View 'Registry32' -KeyName $rootsKeyName |
    Where-Object { $_ -clike '*.*.*.*' }
foreach ($versionSubKeyName in $versionSubKeyNames) {
	# Parse the version.
    $version = $null
    if (!([System.Version]::TryParse($versionSubKeyName, [ref]$version))) {
        continue
    }

	# Save the Windows UAP info (for sorting).
    $windowsUAPSdks += New-Object psobject -Property @{
        Version = $version
    }
}

# Add a capability for the max UAP SDK.
$maxWindowsUAPSdk =
    $windowsUAPSdks |
    Sort-Object -Property Version -Descending |
    Select-Object -First 1
if ($maxWindowsUAPSdk) {
    Write-Capability -Name 'WindowsUAP' -Value $maxWindowsUAPSdk.Version
}