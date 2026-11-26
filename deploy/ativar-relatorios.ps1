#Requires -RunAsAdministrator
<#
  Cada PC passa a deixar no servidor uma linha a dizer o que aconteceu quando correu o instalador
  (instalado, sem sala, sala não ativa, falta o .NET...). Vê-se na consola: Sistema e instalação.
  Útil quando a firewall não deixa o servidor chegar aos PCs (o PC é que escreve no servidor).

    .\ativar-relatorios.ps1
#>
param([string]$Pasta = 'C:\Deploy\SalaDeAula')
$ErrorActionPreference = 'Stop'
$rel = Join-Path $Pasta 'relatorios'
New-Item $rel -ItemType Directory -Force | Out-Null

$dn = [string](([ADSI]'LDAP://RootDSE').defaultNamingContext)
$domSid = (New-Object Security.Principal.SecurityIdentifier(([ADSI]"LDAP://$dn").objectSid[0], 0)).Value
function NomeDe($sid) { (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value }

# NTFS: computadores do domínio podem criar/escrever ficheiros nesta pasta (só nesta)
icacls $rel /grant "*$domSid-515:(OI)(CI)M" | Out-Null
$existe = (net share | Select-String -SimpleMatch 'SalaVivaRelatorios$') -ne $null
if (-not $existe) {
    net share "SalaVivaRelatorios`$=$rel" "/GRANT:$(NomeDe "$domSid-515"),CHANGE" "/GRANT:$(NomeDe 'S-1-5-32-544'),FULL" | Out-Null
}

# instalar.cmd: verifica o .NET (Windows 7) e regista erros no servidor
$srv = $env:COMPUTERNAME
@"
@echo off
rem Script de arranque (GPO). Instala/atualiza o agente SalaViva se este PC estiver numa sala ativa.
set REL=\\$srv\SalaVivaRelatorios$\%COMPUTERNAME%.log
set NETV=0
for /f "tokens=3" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| find "Release"') do set NETV=%%a
if %NETV% LSS 394802 (
  echo %date% %time:~0,8% Falta o .NET Framework 4.6.2 ou mais recente neste PC ^(versao %NETV%^)>>"%REL%" 2>nul
  exit /b 0
)
"%~dp0leve\ClassroomAgent.exe" --instalar
if errorlevel 1 echo %date% %time:~0,8% ERRO: o instalador terminou com o codigo %errorlevel%>>"%REL%" 2>nul
exit /b 0
"@ | Set-Content (Join-Path $Pasta 'instalar.cmd') -Encoding ASCII

Write-Host ''
Write-Host '=== Relatórios dos PCs ativados ===' -ForegroundColor Green
Write-Host "Pasta    : $rel"
Write-Host "Partilha : \\$srv\SalaVivaRelatorios$"
Write-Host 'Na consola: Sistema e instalação > "O que os PCs disseram".'
