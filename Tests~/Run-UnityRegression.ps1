param(
    [ValidateSet('MapAvatar', 'StaticMesh')]
    [string]$Test = 'MapAvatar',
    [string]$UnityEditor = 'C:/Program Files/Unity/Hub/Editor/2021.3.16f1/Editor/Unity.exe'
)
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path ([System.IO.Path]::GetTempPath()) ('WCR-Native-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$editorFolder = Join-Path $testProject 'Assets/Editor'
New-Item -ItemType Directory -Path $editorFolder, (Join-Path $testProject 'Packages'), (Join-Path $testProject 'ProjectSettings') -Force | Out-Null
$manifest = @{ dependencies = @{ 'com.clyppe.crate-ripper' = ('file:' + $repoRoot.Replace('\', '/')) } }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testProject 'Packages/manifest.json') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $testProject 'ProjectSettings/ProjectVersion.txt') -Value 'm_EditorVersion: 2021.3.16f1' -Encoding UTF8
$testClass = if ($Test -eq 'MapAvatar') { 'MapAvatarRecoveryRegression' } else { 'StaticMeshNativeRegression' }
$marker = if ($Test -eq 'MapAvatar') { 'WAFFLES_MAP_AVATAR_REGRESSION_OK' } else { 'WAFFLES_STATIC_NATIVE_OK' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('Unity/' + $testClass + '.cs')) -Destination $editorFolder
if ($Test -eq 'MapAvatar') {
    # These test doubles are for an SDK-free disposable project only.
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Unity/WarehouseFixtures') -Filter '*.cs' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $editorFolder
    }
}
$logPath = Join-Path $testProject 'native-regression.log'
$arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $testProject + '"'), '-executeMethod', ($testClass + '.Run'), '-logFile', ('"' + $logPath + '"'))
$testProcess = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
$testProcess.WaitForExit()
$log = [System.IO.File]::ReadAllText($logPath)
Write-Output ('Test project and log retained at: ' + $testProject)
if ($testProcess.ExitCode -ne 0 -or $log -notmatch $marker) {
    throw ('Native Unity regression failed. Exit=' + $testProcess.ExitCode + '. Inspect: ' + $logPath)
}
Write-Output (($log -split '\r?\n' | Where-Object { $_ -match $marker }) -join [Environment]::NewLine)
