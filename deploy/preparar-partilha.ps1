#Requires -RunAsAdministrator
<#
  Prepara no SERVIDOR a partilha de onde os PCs instalam o agente sozinhos (GPO de arranque).
  Corre dentro da pasta SalaDeAula-Agente (a que tem a pasta "leve"):

    .\preparar-partilha.ps1                          # primeira vez (ativa só a Sala TIC1)
    .\preparar-partilha.ps1 -Salas 'Sala TIC1','Sala TIC 2'
    .\preparar-partilha.ps1 -Salas '*'               # todas as salas
    .\preparar-partilha.ps1                          # depois de descarregar uma versão nova: atualiza os ficheiros

  Cria:
    C:\Deploy\SalaDeAula\leve\...        agente
    C:\Deploy\SalaDeAula\instalar.json   chave, servidor, salas ativas, nomes das salas
    C:\Deploy\SalaDeAula\instalar.cmd    script de arranque para a GPO
    \\SERVIDOR\SalaDeAula$               partilha (só leitura; só os computadores do domínio e administradores)
#>
param(
    [string[]]$Salas,
    [hashtable]$NomesSalas,
    # Base com uma pasta por escola (EBVG, ESEQ, EBPN). As salas ativas e os nomes gerem-se na consola web.
    [string]$PastaSalas = 'OU=_Computadores MAC AEEQ,DC=queiroz,DC=local',
    [string]$SoAbaixoDe = '',
    [string]$Pasta = 'C:\Deploy\SalaDeAula',
    [string]$Partilha = 'SalaDeAula$',
    [string]$ServerDir = 'C:\ClassroomServer',
    [string]$ServerUrl = ''
)
$ErrorActionPreference = 'Stop'
$leve = Join-Path $PSScriptRoot 'leve'
if (-not (Test-Path (Join-Path $leve 'ClassroomAgent.exe'))) { throw "Não encontro $leve. Corre dentro da pasta SalaDeAula-Agente." }

# ------------------------------------------------------------------ ficheiros
New-Item "$Pasta\leve" -ItemType Directory -Force | Out-Null
Copy-Item "$leve\*" "$Pasta\leve\" -Force
# o instalador "já" (usado pelo botão da consola, através da tarefa SalaVivaInstalar)
Copy-Item (Join-Path $PSScriptRoot 'instalar-agora.ps1') $Pasta -Force
Get-ChildItem $Pasta -Recurse | Unblock-File

$key = (& "$ServerDir\ClassroomServer.exe" show-key | Select-Object -Last 1).Trim()
$pin = (Get-Content "$ServerDir\data\certificado-pin.txt" | Select-Object -First 1).Trim()
$cfgSrv = Get-Content "$ServerDir\data\classroom-server.json" -Raw | ConvertFrom-Json
if (-not $ServerUrl) {
    $ip = Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' |
        Sort-Object { -not $_.DefaultIPGateway } |
        ForEach-Object { $_.IPAddress } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' } |
        Select-Object -First 1
    $ServerUrl = "wss://${ip}:$($cfgSrv.Port)/ws/agent"
}

# mantém salas ativas e nomes já definidos, a não ser que sejam passados agora
$jsonPath = "$Pasta\instalar.json"
$antigo = if (Test-Path $jsonPath) { Get-Content $jsonPath -Raw | ConvertFrom-Json } else { $null }
if (-not $Salas) { $Salas = if ($antigo) { @($antigo.Salas) } else { @('EBVG/Sala TIC1') } }
$nomes = [ordered]@{}
if ($antigo -and $antigo.NomesSalas) { $antigo.NomesSalas.PSObject.Properties | ForEach-Object { $nomes[$_.Name] = $_.Value } }
if ($NomesSalas) { foreach ($k in $NomesSalas.Keys) { $nomes[$k] = $NomesSalas[$k] } }

$cfg = [ordered]@{
    ServerUrls        = @("pipe://$($env:COMPUTERNAME)/SalaDeAula", $ServerUrl)
    EnrollmentKey     = $key
    CertificatePin    = $pin
    PastaSalas        = $PastaSalas
    SoAbaixoDe        = $SoAbaixoDe
    Salas             = @($Salas)
    NomesSalas        = $nomes
    ShowViewIndicator = $true
}
$cfg | ConvertTo-Json -Depth 5 | Set-Content $jsonPath -Encoding UTF8

@'
@echo off
rem Script de arranque (GPO). Instala/atualiza o agente Sala de Aula se este PC estiver numa sala ativa.
"%~dp0leve\ClassroomAgent.exe" --instalar
exit /b 0
'@ | Set-Content "$Pasta\instalar.cmd" -Encoding ASCII

# ------------------------------------------------------------------ permissões (por SID: funciona em qualquer idioma)
$dn = [string](([ADSI]'LDAP://RootDSE').defaultNamingContext)
$dom = [ADSI]"LDAP://$dn"
$domSid = (New-Object Security.Principal.SecurityIdentifier($dom.objectSid[0], 0)).Value
# SYSTEM, Administradores, Admins do domínio: total | Computadores do domínio: leitura
icacls $Pasta /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*$domSid-512:(OI)(CI)F" "*$domSid-515:(OI)(CI)RX" | Out-Null

function NomeDe($sid) { (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value }
$todos = NomeDe 'S-1-1-0'
$admins = NomeDe 'S-1-5-32-544'
$existe = (net share | Select-String -SimpleMatch $Partilha) -ne $null
if (-not $existe) {
    net share "$Partilha=$Pasta" "/GRANT:$todos,READ" "/GRANT:$admins,FULL" | Out-Null
}

# relatórios dos PCs (e instalar.cmd com verificação do .NET)
if (Test-Path "$PSScriptRoot\ativar-relatorios.ps1") { & "$PSScriptRoot\ativar-relatorios.ps1" -Pasta $Pasta | Out-Null }

Write-Host ''
Write-Host '=== Partilha pronta ===' -ForegroundColor Green
Write-Host "Script de arranque para a GPO : \\$($env:COMPUTERNAME)\$Partilha\instalar.cmd"
Write-Host "Salas ativas                  : $(@($Salas) -join ', ')"
Write-Host "Nomes na consola              : $(($nomes.Keys | ForEach-Object { "$_ -> $($nomes[$_])" }) -join '; ')"
Write-Host "Servidor                      : $($cfg.ServerUrls -join '  |  ')"
