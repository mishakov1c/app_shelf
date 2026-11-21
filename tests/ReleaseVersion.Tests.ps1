# Check the built release candidate without starting it or opening user data.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$metadata = [IO.File]::ReadAllText((Join-Path $root 'VersionInfo.cs'))
$version = [regex]::Match($metadata, 'AssemblyInformationalVersion\("((\d+\.\d+\.\d+)(?:-rc\.[1-9]\d*)?)"\)')
if (-not $version.Success) { throw 'Declared application version missing from VersionInfo.cs.' }
$expected = $version.Groups[1].Value
$numeric = $version.Groups[2].Value
$exe = Join-Path $root 'AppShelf.exe'
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($exe)
if ($assembly.GetName().Version.ToString() -ne "$numeric.0") {
    throw "Assembly version must be $numeric.0; got $($assembly.GetName().Version)."
}
$attributes = [Reflection.CustomAttributeData]::GetCustomAttributes($assembly)
$values = @{}
foreach ($attribute in $attributes) {
    $name = $attribute.Constructor.DeclaringType.FullName
    if ($name -eq 'System.Reflection.AssemblyMetadataAttribute') {
        $values[$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
    }
    elseif ($name -eq 'System.Reflection.AssemblyInformationalVersionAttribute') {
        $values['InformationalVersion'] = $attribute.ConstructorArguments[0].Value
    }
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($fileVersion -ne "$numeric.0" -or $values['InformationalVersion'] -ne $expected) {
    throw "File/informational version must match $expected."
}
if ($values['AppShelf.ReleaseNotesVersion'] -ne $numeric) {
    throw 'Embedded release notes do not match the candidate version.'
}
$notes = [regex]::Match($metadata, '(?s)AssemblyMetadata\("AppShelf\.ReleaseNotes",\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+)\)')
if (-not $notes.Success) { throw 'Declared release notes missing from VersionInfo.cs.' }
$expectedNotes = -join @([regex]::Matches($notes.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object {
    [regex]::Unescape($_.Groups[1].Value)
})
if ([string]::IsNullOrWhiteSpace($values['AppShelf.ReleaseNotes']) -or
    $values['AppShelf.ReleaseNotes'] -cne $expectedNotes) {
    throw 'Embedded release notes differ from the declared release notes.'
}
Write-Output "PASS release version, file metadata and matching declared release notes: $expected"
