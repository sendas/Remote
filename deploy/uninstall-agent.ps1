#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'
Stop-Service ClassroomAgent -Force
Get-Process ClassroomAgent | Stop-Process -Force
sc.exe delete ClassroomAgent | Out-Null
Remove-Item (Join-Path $env:ProgramFiles 'ClassroomAgent') -Recurse -Force
Remove-Item (Join-Path $env:ProgramData 'ClassroomAgent') -Recurse -Force
Write-Host 'Agente removido.'
