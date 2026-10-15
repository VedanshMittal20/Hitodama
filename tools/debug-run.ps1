$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $PSScriptRoot "..\publish\Shell\Companion.Shell.exe"
$psi.WorkingDirectory = Join-Path $PSScriptRoot "..\publish\Shell"
$psi.RedirectStandardError = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false

$p = [System.Diagnostics.Process]::Start($psi)
$out = $p.StandardOutput.ReadToEnd()
$err = $p.StandardError.ReadToEnd()
$p.WaitForExit(3000)
Write-Host "HasExited: $($p.HasExited)"
Write-Host "ExitCode: $($p.ExitCode)"
Write-Host "OUT: $out"
Write-Host "ERR: $err"
