#Requires -RunAsAdministrator
<#
  Instala (ou remove) o agente num PC da rede, a partir do SERVIDOR, sem ir ao PC.
  Corre no servidor onde está o ClassroomServer, dentro da pasta SalaDeAula-Agente:

      .\install-remote.ps1 -Computer DESKTOP-16PB879
      .\install-remote.ps1 -Computer DESKTOP-16PB879 -Room TIC1
      .\install-remote.ps1 -Computer DESKTOP-16PB879 -Uninstall

  Lê sozinho a EnrollmentKey e o CertificatePin do servidor (C:\ClassroomServer).
  Requisitos no PC: ligado, Windows 10/11, partilha administrativa C$ acessível (normal num domínio).
#>
param(
    [Parameter(Mandatory)][string]$Computer,
    [string]$Room = 'TESTE',
    [string]$ServerUrl = '',
    [string]$ServerDir = 'C:\ClassroomServer',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$pc = $Computer

function Passo($t) { Write-Host "`n>> $t" -ForegroundColor Cyan }

Passo "A verificar se $pc está ligado"
$c = New-Object Net.Sockets.TcpClient
try {
    $ar = $c.BeginConnect($pc, 445, $null, $null)
    $ok = $ar.AsyncWaitHandle.WaitOne(3000) -and $c.Connected
} catch { $ok = $false }
$c.Close()
if (-not $ok) { throw "$pc não responde (desligado, nome errado ou firewall a bloquear a partilha de ficheiros)." }
Write-Host "   OK"

$prog = "\\$pc\C$\Program Files\ClassroomAgent"
$data = "\\$pc\C$\ProgramData\ClassroomAgent"

if ($Uninstall) {
    Passo "A remover o agente de $pc"
    sc.exe \\$pc stop ClassroomAgent | Out-Null
    Start-Sleep 3
    sc.exe \\$pc delete ClassroomAgent | Out-Null
    # remove a regra "Sem Internet" no PC remoto, se tiver ficado para trás
    $env:SALA_PC = $pc
    sc.exe --% \\%SALA_PC% create SalaVivaNet binPath= "cmd /c netsh advfirewall firewall delete rule name=\"SalaViva - Sem Internet\"" start= demand
    sc.exe \\$pc start SalaVivaNet | Out-Null
    Start-Sleep 3
    sc.exe \\$pc delete SalaVivaNet | Out-Null
    Remove-Item $prog, $data -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "   Removido." -ForegroundColor Green
    return
}

Passo "A verificar a versão do Windows"
if (-not (Test-Path "\\$pc\C$\Windows\SystemApps")) {
    throw "$pc não parece ter Windows 10/11 (este agente não funciona em Windows 7/8)."
}
Write-Host "   Windows 10/11"

Passo "A ler a chave e o certificado do servidor"
$key = (& "$ServerDir\ClassroomServer.exe" show-key | Select-Object -Last 1).Trim()
$pin = (Get-Content "$ServerDir\data\certificado-pin.txt" | Select-Object -First 1).Trim()
$cfgSrv = Get-Content "$ServerDir\data\classroom-server.json" -Raw | ConvertFrom-Json
if (-not $ServerUrl) {
    # placa com gateway predefinido primeiro (servidores com várias placas de rede)
    $ip = Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' |
        Sort-Object { -not $_.DefaultIPGateway } |
        ForEach-Object { $_.IPAddress } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' } |
        Select-Object -First 1
    $ServerUrl = "wss://${ip}:$($cfgSrv.Port)/ws/agent"
}
# 1.º o canal SMB (porta 445, já aberta para os PCs do domínio), depois o HTTPS
$urls = @("pipe://$($env:COMPUTERNAME)/SalaDeAula", $ServerUrl)
Write-Host "   Servidor: $($urls -join '  |  ')"

Passo "A copiar o agente (≈70 MB, pode demorar um minuto)"
$exe = Join-Path $PSScriptRoot 'ClassroomAgent.exe'
if (-not (Test-Path $exe)) { throw "Não encontro $exe. Corre este script dentro da pasta SalaDeAula-Agente." }
sc.exe \\$pc stop ClassroomAgent | Out-Null
Start-Sleep 2
New-Item $prog -ItemType Directory -Force | Out-Null
New-Item $data -ItemType Directory -Force | Out-Null
Copy-Item $exe $prog -Force

$json = @"
{
  "ServerUrls": ["$($urls[0])", "$($urls[1])"],
  "EnrollmentKey": "$key",
  "CertificatePin": "$pin",
  "Room": "$Room",
  "ShowViewIndicator": true
}
"@
Set-Content "$data\agent.json" $json -Encoding UTF8
icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

Passo "A criar e arrancar o serviço"
$env:SALA_PC = $pc
$existe = (sc.exe \\$pc query ClassroomAgent | Select-String 'SERVICE_NAME') -ne $null
if (-not $existe) {
    # --% passa o resto da linha tal e qual ao sc.exe (só expande %VAR%), evitando problemas de aspas
    sc.exe --% \\%SALA_PC% create ClassroomAgent binPath= "\"C:\Program Files\ClassroomAgent\ClassroomAgent.exe\"" start= auto DisplayName= "Sala de Aula - Agente"
}
sc.exe \\$pc failure ClassroomAgent reset= 0 actions= restart/2000/restart/2000/restart/2000 | Out-Null
sc.exe \\$pc sdset ClassroomAgent 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null
sc.exe \\$pc start ClassroomAgent | Out-Null

Start-Sleep 12
# "4  RUNNING" aparece igual em qualquer idioma do Windows (STATE/ESTADO muda)
$estado = sc.exe \\$pc query ClassroomAgent | Select-String 'RUNNING|STOPPED|PENDING'
Write-Host "   $estado"

Passo "Últimas linhas do registo do agente"
Get-Content "$data\agent.log" -ErrorAction SilentlyContinue | Select-Object -Last 8

if ("$estado" -match 'RUNNING') {
    Write-Host "`nPronto. Abre a consola e escolhe Sala -> $Room." -ForegroundColor Green
} else {
    Write-Host "`nO serviço não ficou a correr. Envia estas linhas ao suporte." -ForegroundColor Yellow
}
