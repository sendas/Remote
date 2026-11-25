#Requires -RunAsAdministrator
<#
  Instala o agente num PC de aluno (Windows 10/11). Pode ser usado como script de arranque de GPO
  (Configuração do Computador > Políticas > Definições do Windows > Scripts > Arranque).

  Exemplo:
    .\install-agent.ps1 -ServerUrl 'wss://srv-escola.escola.local:8443/ws/agent' `
                        -EnrollmentKey 'XXXX' -CertificatePin 'ABCD...' -Room 'Sala 12'

  -ServerUrl aceita vários endereços separados por vírgula (tentados por ordem).
  -Room vazio: usa o prefixo do nome do PC (SALA12-PC05 -> SALA12).
  -Source: pasta (ex.: partilha \\servidor\deploy$\agent) onde está o ClassroomAgent.exe.
#>
param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$EnrollmentKey,
    [string]$CertificatePin = '',
    [string]$Room = '',
    [switch]$HideViewIndicator,
    [string]$Source = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$svc = 'ClassroomAgent'
$dir = Join-Path $env:ProgramFiles 'ClassroomAgent'
$exe = Join-Path $dir 'ClassroomAgent.exe'
$dataDir = Join-Path $env:ProgramData 'ClassroomAgent'
$src = Join-Path $Source 'ClassroomAgent.exe'

# Só atualiza se o executável mudou (barato para correr em cada arranque via GPO)
$needCopy = -not (Test-Path $exe) -or ((Get-FileHash $src).Hash -ne (Get-FileHash $exe).Hash)
if ($needCopy -and (Get-Service $svc -ErrorAction SilentlyContinue)) {
    Stop-Service $svc -Force
    Get-Process ClassroomAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
}
New-Item $dir -ItemType Directory -Force | Out-Null
if ($needCopy) { Copy-Item $src $exe -Force }

# Configuração: a chave é secreta -> só SYSTEM e Administradores conseguem ler
New-Item $dataDir -ItemType Directory -Force | Out-Null
icacls $dataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
$cfg = [ordered]@{
    ServerUrls        = @($ServerUrl -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    EnrollmentKey     = $EnrollmentKey
    CertificatePin    = $CertificatePin
    Room              = $Room
    ShowViewIndicator = -not $HideViewIndicator
}
$cfg | ConvertTo-Json | Set-Content (Join-Path $dataDir 'agent.json') -Encoding UTF8

if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'Sala de Aula - Agente' `
        -Description 'Permite ao professor ver, bloquear e enviar mensagens a este computador.' `
        -StartupType Automatic | Out-Null
}
# Reinicia sempre que falhar; impede utilizadores normais de o parar (só Administradores/SYSTEM)
sc.exe failure $svc reset= 0 actions= restart/2000/restart/2000/restart/2000 | Out-Null
sc.exe sdset $svc 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null

Start-Service $svc -ErrorAction SilentlyContinue
if ((Get-Service $svc).Status -ne 'Running') { Restart-Service $svc }
Write-Host "Agente instalado e a correr. Log: $dataDir\agent.log" -ForegroundColor Green
