#Requires -RunAsAdministrator
<#
  Instala JÁ nos PCs que estão ligados (sem esperar que reiniciem), usando a partilha preparada
  com preparar-partilha.ps1. Cada PC corre o mesmo instalador da GPO: descobre a sua sala pelo MAC
  e só instala se estiver numa sala ativa (os outros PCs não ficam com nada).

    .\instalar-agora.ps1                    # todos os PCs Windows 7-11 do domínio que estejam ligados
    .\instalar-agora.ps1 -Filtro 'EBVG*'    # só nomes começados por EBVG
    .\instalar-agora.ps1 -Computadores EBVGPCT1,EBVGPCT4

  Relatório em C:\ClassroomServer\data\instalar-agora-<data>.csv
#>
param(
    [string[]]$Computadores = @(),
    [string]$Filtro = '*',
    [string]$Partilha = "\\$($env:COMPUTERNAME)\SalaDeAula$"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path "$Partilha\instalar.cmd")) { throw "Não encontro $Partilha\instalar.cmd. Corre primeiro .\preparar-partilha.ps1" }

if (-not $Computadores.Count) {
    $s = New-Object DirectoryServices.DirectorySearcher "(&(objectCategory=computer)(name=$Filtro)(|(operatingSystem=Windows 7*)(operatingSystem=Windows 8*)(operatingSystem=Windows 10*)(operatingSystem=Windows 11*)))"
    $s.PageSize = 1000
    [void]$s.PropertiesToLoad.Add('name')
    $Computadores = $s.FindAll() | ForEach-Object { [string]$_.Properties['name'][0] } | Sort-Object
}
Write-Host "$($Computadores.Count) PCs a verificar." -ForegroundColor Cyan

function Ligado($pc) {
    $c = New-Object Net.Sockets.TcpClient
    try { $ar = $c.BeginConnect($pc, 445, $null, $null); return ($ar.AsyncWaitHandle.WaitOne(1200) -and $c.Connected) }
    catch { return $false }
    finally { $c.Close() }
}

$env:SALA_CMD = "$Partilha\instalar.cmd"
$rel = @()
$i = 0
foreach ($pc in $Computadores) {
    $i++
    Write-Host ("[{0}/{1}] {2,-22} " -f $i, $Computadores.Count, $pc) -NoNewline
    $res = ''
    try {
        if (-not (Ligado $pc)) { throw 'desligado ou inacessível' }
        $log = "\\$pc\C$\ProgramData\ClassroomAgent\instalar.log"
        $antes = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

        # serviço temporário que corre o instalador como SYSTEM nesse PC e é apagado logo a seguir
        $env:SALA_PC = $pc
        sc.exe \\$pc delete SalaDeAulaInstalar | Out-Null
        sc.exe --% \\%SALA_PC% create SalaDeAulaInstalar binPath= "cmd.exe /c \"%SALA_CMD%\"" start= demand
        sc.exe \\$pc start SalaDeAulaInstalar | Out-Null

        $res = 'sem resposta (ver instalar.log no PC)'
        for ($t = 0; $t -lt 40; $t++) {
            Start-Sleep 1
            if ((Test-Path $log) -and (Get-Item $log).Length -gt $antes) {
                $res = (Get-Content $log | Select-Object -Last 1) -replace '^\S+ \S+ ', ''
                break
            }
        }
        sc.exe \\$pc delete SalaDeAulaInstalar | Out-Null
    }
    catch { $res = $_.Exception.Message }
    $cor = if ($res -like 'Instalado*' -or $res -like 'Já instalado*') { 'Green' } elseif ($res -like 'ERRO*') { 'Red' } else { 'DarkGray' }
    Write-Host $res -ForegroundColor $cor
    $rel += New-Object psobject -Property ([ordered]@{ PC = $pc; Resultado = $res })
}

$csv = "C:\ClassroomServer\data\instalar-agora-{0:yyyyMMdd-HHmm}.csv" -f (Get-Date)
$rel | Export-Csv $csv -NoTypeInformation -Encoding UTF8 -Delimiter ';'
Write-Host ''
Write-Host '=== Resumo ===' -ForegroundColor Cyan
$rel | Group-Object { ($_.Resultado -split "[('.]")[0].Trim() } | Sort-Object Count -Descending | ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }
Write-Host "Relatório: $csv"
