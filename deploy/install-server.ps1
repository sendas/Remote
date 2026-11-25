#Requires -RunAsAdministrator
# Nota: usa SIDs (S-1-5-18 = SYSTEM, S-1-5-32-544 = Administradores) para funcionar em Windows em qualquer idioma.
<#
  Instala o ClassroomServer como serviço Windows no servidor da escola.
  Executar a partir da pasta publish\server:
      .\install-server.ps1 [-Port 8443] [-InstallDir C:\ClassroomServer]
#>
param(
    [int]$Port = 8443,
    [string]$InstallDir = 'C:\ClassroomServer'
)
$ErrorActionPreference = 'Stop'
$svc = 'ClassroomServer'

if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Stop-Service $svc -Force
    Start-Sleep 2
}

New-Item $InstallDir -ItemType Directory -Force | Out-Null
Copy-Item "$PSScriptRoot\ClassroomServer.exe" $InstallDir -Force
Copy-Item "$PSScriptRoot\wwwroot" $InstallDir -Recurse -Force

# Pasta de dados (configuração, certificado, auditoria): só SYSTEM e Administradores
$data = Join-Path $InstallDir 'data'
New-Item $data -ItemType Directory -Force | Out-Null
icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

# Primeira execução: gera configuração, palavra-passe do admin, chave dos agentes e certificado
$exe = Join-Path $InstallDir 'ClassroomServer.exe'
& $exe list-teachers | Out-Null
& $exe show-pin | Out-Null

$cfgPath = Join-Path $data 'classroom-server.json'
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.Port = $Port
$cfg | ConvertTo-Json -Depth 5 | Set-Content $cfgPath -Encoding UTF8

if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'Sala de Aula - Servidor' `
        -Description 'Relay entre a consola do professor e os PCs dos alunos.' -StartupType Automatic | Out-Null
}
sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

# Firewall: abre a porta (restringe depois às VLANs dos alunos/professores, se quiseres)
Get-NetFirewallRule -DisplayName 'Sala de Aula (servidor)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'Sala de Aula (servidor)' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null

Start-Service $svc

$pin = & $exe show-pin
$key = & $exe show-key
Write-Host ''
Write-Host '=== Servidor instalado ===' -ForegroundColor Green
Write-Host "Consola do professor : https://$($env:COMPUTERNAME):$Port/"
Write-Host "EnrollmentKey        : $key"
Write-Host "CertificatePin       : $pin"
$cred = Join-Path $data 'LEIA-ME-credenciais-iniciais.txt'
if (Test-Path $cred) { Write-Host ''; Get-Content $cred; Write-Host "(apaga $cred depois de mudares a palavra-passe)" -ForegroundColor Yellow }
Write-Host ''
Write-Host 'Agentes:  .\install-agent.ps1 -ServerUrl wss://SERVIDOR:' $Port '/ws/agent -EnrollmentKey <key> -CertificatePin <pin>'
