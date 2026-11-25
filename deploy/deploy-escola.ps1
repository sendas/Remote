#Requires -RunAsAdministrator
<#
  Instala o agente Sala de Aula em massa, a partir do SERVIDOR, com a sala de cada PC tirada do Active Directory.

  Como descobre as salas:
    No AD existe uma árvore de pastas por sala (ex.: _Computadores MAC AEEQ > EBVG > Alunos > Salas TIC > Sala TIC1)
    com uma conta por PC, com o MESMO NOME do computador (EBVGPCT1, EBVGPC13, ...).
    A sala de cada PC = nome da pasta onde está essa conta.

  Uso (na pasta SalaDeAula-Agente, PowerShell como Administrador):

    # 1) Ver o plano, sem instalar nada (RECOMENDADO primeiro):
    .\deploy-escola.ps1 -Simular

    # 2) Instalar numa sala só:
    .\deploy-escola.ps1 -Salas 'Sala TIC1'

    # 3) Instalar em todas as salas de alunos da EBVG:
    .\deploy-escola.ps1

  Pode ser corrido as vezes que forem precisas: PCs já instalados e iguais são saltados;
  PCs desligados ficam para a próxima vez. Relatório em C:\ClassroomServer\data\deploy-<data>.csv
#>
param(
    # Pasta do AD com a árvore de salas (contas com o nome dos PCs)
    [string]$PastaSalas = 'OU=EBVG,OU=_Computadores MAC AEEQ,DC=queiroz,DC=local',
    # Só PCs abaixo de uma pasta com este nome (vazio = todos, incluindo professores/secretaria)
    [string]$SoAbaixoDe = 'Alunos',
    # Limitar a estas salas (nomes das pastas do AD)
    [string[]]$Salas = @(),
    # Limitar a estes PCs
    [string[]]$Computadores = @(),
    [switch]$Simular,
    [string]$ServerUrl = '',
    [string]$ServerDir = 'C:\ClassroomServer'
)
$ErrorActionPreference = 'Stop'
$aqui = $PSScriptRoot
# Versão "leve" do agente (.NET Framework, ~1 MB): serve Windows 7, 8.1, 10 e 11.
# O Windows 10/11 já traz o .NET Framework 4.8; no Windows 7 tem de estar instalado (4.6.2 ou mais recente).
$origem = Join-Path $aqui 'leve'
if (-not (Test-Path (Join-Path $origem 'ClassroomAgent.exe'))) {
    throw "Não encontro $origem\ClassroomAgent.exe. Corre o script dentro da pasta SalaDeAula-Agente (versão 1.1 ou mais recente)."
}

# ------------------------------------------------------------------ chave, pin e endereços do servidor
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
$pipeUrl = "pipe://$($env:COMPUTERNAME)/SalaDeAula"

# ------------------------------------------------------------------ salas a partir do AD
function Pesquisar($raiz, $filtro, $props) {
    $s = New-Object DirectoryServices.DirectorySearcher
    $s.SearchRoot = New-Object DirectoryServices.DirectoryEntry("LDAP://$raiz")
    $s.Filter = $filtro
    $s.PageSize = 1000
    foreach ($p in $props) { [void]$s.PropertiesToLoad.Add($p) }
    $s.FindAll()
}

Write-Host "A ler as salas do AD em $PastaSalas ..." -ForegroundColor Cyan
$mapa = @{}
foreach ($r in (Pesquisar $PastaSalas '(|(objectClass=user)(objectClass=computer))' @('name', 'distinguishedName'))) {
    $nome = [string]$r.Properties['name'][0]
    $dn = [string]$r.Properties['distinguishedname'][0]
    $partes = $dn -split ',(?=(?:OU|CN|DC)=)'
    $sala = ($partes[1] -replace '^(OU|CN)=', '')
    $pastas = $partes | Where-Object { $_ -like 'OU=*' } | ForEach-Object { $_.Substring(3) }
    if ($SoAbaixoDe -and ($pastas -notcontains $SoAbaixoDe)) { continue }
    if ($Salas.Count -and ($Salas -notcontains $sala)) { continue }
    if ($Computadores.Count -and ($Computadores -notcontains $nome)) { continue }
    $mapa[$nome.ToUpper()] = $sala
}
Write-Host "  $($mapa.Count) PCs encontrados em $(@($mapa.Values | Sort-Object -Unique).Count) salas."

$domRaiz = ([ADSI]'LDAP://RootDSE').defaultNamingContext
$contas = @{}
foreach ($r in (Pesquisar $domRaiz '(objectCategory=computer)' @('name', 'operatingSystem'))) {
    $contas[([string]$r.Properties['name'][0]).ToUpper()] = [string]$r.Properties['operatingsystem'][0]
}

# ------------------------------------------------------------------ funções de instalação
function Ligado($pc) {
    $c = New-Object Net.Sockets.TcpClient
    try { $ar = $c.BeginConnect($pc, 445, $null, $null); return ($ar.AsyncWaitHandle.WaitOne(1500) -and $c.Connected) }
    catch { return $false }
    finally { $c.Close() }
}

function VersaoDotNet($pc) {
    foreach ($d in 'Framework64', 'Framework') {
        $f = "\\$pc\C$\Windows\Microsoft.NET\$d\v4.0.30319\clr.dll"
        if (Test-Path $f) { return [version]((Get-Item $f).VersionInfo.FileVersion -split ' ')[0] }
    }
    return $null
}

function EstadoServico($pc) {
    $q = sc.exe \\$pc query ClassroomAgent | Out-String
    if ($q -match 'RUNNING') { 'RUNNING' } elseif ($q -match 'STOPPED') { 'STOPPED' } elseif ($q -match '1060') { 'NAO_EXISTE' } else { 'OUTRO' }
}

function Instalar($pc, $sala) {
    $prog = "\\$pc\C$\Program Files\ClassroomAgent"
    $data = "\\$pc\C$\ProgramData\ClassroomAgent"
    $json = @"
{
  "ServerUrls": ["$pipeUrl", "$ServerUrl"],
  "EnrollmentKey": "$key",
  "CertificatePin": "$pin",
  "Room": "$($sala -replace '"', '')",
  "ShowViewIndicator": true
}
"@
    # já está instalado, igual e a correr? então não mexe
    $exeDest = Join-Path $prog 'ClassroomAgent.exe'
    $cfgDest = Join-Path $data 'agent.json'
    $estado = EstadoServico $pc
    if ($estado -eq 'RUNNING' -and (Test-Path $exeDest) -and (Test-Path $cfgDest) -and
        (Get-FileHash $exeDest).Hash -eq (Get-FileHash (Join-Path $origem 'ClassroomAgent.exe')).Hash -and
        ((Get-Content $cfgDest -Raw).Trim() -eq $json.Trim())) {
        return 'já instalado'
    }

    if ($estado -ne 'NAO_EXISTE') {
        sc.exe \\$pc stop ClassroomAgent | Out-Null
        for ($i = 0; $i -lt 15 -and (EstadoServico $pc) -eq 'RUNNING'; $i++) { Start-Sleep 1 }
        Start-Sleep 2
    }
    New-Item $prog -ItemType Directory -Force | Out-Null
    New-Item $data -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $origem '*') $prog -Force
    Set-Content $cfgDest $json -Encoding UTF8
    icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

    if ($estado -eq 'NAO_EXISTE') {
        $env:SALA_PC = $pc
        # --% passa o resto da linha tal e qual ao sc.exe (só expande %VAR%)
        sc.exe --% \\%SALA_PC% create ClassroomAgent binPath= "\"C:\Program Files\ClassroomAgent\ClassroomAgent.exe\"" start= auto DisplayName= "Sala de Aula - Agente"
    }
    sc.exe \\$pc failure ClassroomAgent reset= 0 actions= restart/2000/restart/2000/restart/2000 | Out-Null
    sc.exe \\$pc sdset ClassroomAgent 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null
    sc.exe \\$pc start ClassroomAgent | Out-Null
    for ($i = 0; $i -lt 10 -and (EstadoServico $pc) -ne 'RUNNING'; $i++) { Start-Sleep 1 }
    if ((EstadoServico $pc) -eq 'RUNNING') { 'instalado' } else { 'ERRO: serviço não arrancou' }
}

# ------------------------------------------------------------------ ciclo principal
$relatorio = @()
$nomes = $mapa.Keys | Sort-Object
$i = 0
foreach ($pc in $nomes) {
    $i++
    $sala = $mapa[$pc]
    $os = $contas[$pc]
    $linha = [ordered]@{ PC = $pc; Sala = $sala; Windows = $os; DotNet = ''; Resultado = '' }
    Write-Host ("[{0}/{1}] {2,-16} {3,-22} " -f $i, $nomes.Count, $pc, $sala) -NoNewline
    try {
        if ($null -eq $os) { throw 'sem conta de computador no AD (nome diferente ou fora do domínio)' }
        if ($os -notmatch 'Windows (7|8|10|11)') { throw "sistema não suportado: $os" }
        if ($Simular) { $linha.Resultado = 'simulação'; Write-Host 'simulação' -ForegroundColor DarkGray; $relatorio += New-Object psobject -Property $linha; continue }
        if (-not (Ligado $pc)) { throw 'desligado ou inacessível' }

        $net = VersaoDotNet $pc
        $linha.DotNet = "$net"
        if ($null -eq $net -or $net -lt [version]'4.6.1586') { throw "falta o .NET Framework 4.8 (tem: $net)" }
        $linha.Resultado = Instalar $pc $sala
        $cor = if ($linha.Resultado -like 'ERRO*') { 'Red' } else { 'Green' }
        Write-Host $linha.Resultado -ForegroundColor $cor
    }
    catch {
        $linha.Resultado = "$($_.Exception.Message)"
        Write-Host $linha.Resultado -ForegroundColor Yellow
    }
    $relatorio += New-Object psobject -Property $linha
}

$csv = Join-Path "$ServerDir\data" ("deploy-{0:yyyyMMdd-HHmm}.csv" -f (Get-Date))
$relatorio | Select-Object PC, Sala, Windows, DotNet, Resultado | Export-Csv $csv -NoTypeInformation -Encoding UTF8 -Delimiter ';'
Write-Host ''
Write-Host '=== Resumo ===' -ForegroundColor Cyan
$relatorio | Group-Object { ($_.Resultado -split ' \(')[0] } | Sort-Object Count -Descending |
    ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }
Write-Host "Relatório: $csv (abre no Excel)"
