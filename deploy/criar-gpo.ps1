#Requires -RunAsAdministrator
<#
  Cria (ou atualiza) a GPO "Sala de Aula - Agente" sem passar pela consola:
   - script de arranque \\SERVIDOR\SalaDeAula$\instalar.cmd
   - "Aguardar sempre pela rede no arranque" (para o script correr com rede)
   - aplicada a "Computadores do domínio" (não aos controladores de domínio)
   - ligada à raiz do domínio
  O instalador só instala nos PCs que estão numa sala ativa (instalar.json); os outros não ficam com nada.
#>
param(
    [string]$Nome = 'Sala de Aula - Agente',
    [string]$Script = "\\$($env:COMPUTERNAME)\SalaDeAula$\instalar.cmd"
)
$ErrorActionPreference = 'Stop'
Import-Module GroupPolicy

$dn = [string](([ADSI]'LDAP://RootDSE').defaultNamingContext)
$dominio = (Get-WmiObject Win32_ComputerSystem).Domain

$gpo = Get-GPO -Name $Nome -ErrorAction SilentlyContinue
if (-not $gpo) { $gpo = New-GPO -Name $Nome -Comment 'Instala o agente Sala de Aula nos PCs das salas (script de arranque).' }

# 1) Aguardar sempre pela rede no arranque
Set-GPRegistryValue -Name $Nome -Key 'HKLM\Software\Policies\Microsoft\Windows NT\CurrentVersion\Winlogon' `
    -ValueName SyncForegroundPolicy -Type DWord -Value 1 | Out-Null

# 2) Script de arranque (scripts.ini no SYSVOL, em Unicode como a consola o grava)
$guid = '{' + $gpo.Id.ToString().ToUpper() + '}'
$sysvol = "\\$dominio\SYSVOL\$dominio\Policies\$guid"
$sdir = "$sysvol\Machine\Scripts"
New-Item "$sdir\Startup", "$sdir\Shutdown" -ItemType Directory -Force | Out-Null
"`r`n[Startup]`r`n0CmdLine=$Script`r`n0Parameters=`r`n" | Set-Content "$sdir\scripts.ini" -Encoding Unicode

# 3) Registar as extensões (registo + scripts) e subir a versão para os PCs aplicarem
$ad = [ADSI]"LDAP://CN=$guid,CN=Policies,CN=System,$dn"
$ad.Put('gPCMachineExtensionNames',
    '[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}][{42B5FAAE-6536-11D2-AE5A-0000F87571E3}{40B6664F-4972-11D1-A7CA-0000F87571E3}]')
$versao = [int]$ad.versionNumber.Value + 1
$ad.Put('versionNumber', $versao)
$ad.SetInfo()
(Get-Content "$sysvol\GPT.INI") -replace '^Version=\d+', "Version=$versao" | Set-Content "$sysvol\GPT.INI" -Encoding ASCII

# 4) Permissões: todos lêem, só "Computadores do domínio" aplicam (nomes obtidos por SID: qualquer idioma)
function NomeGrupo($sid) {
    ((New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value -split '\\')[-1]
}
$domSid = (New-Object Security.Principal.SecurityIdentifier(([ADSI]"LDAP://$dn").objectSid[0], 0)).Value
try {
    Set-GPPermissions -Name $Nome -TargetName (NomeGrupo "$domSid-515") -TargetType Group -PermissionLevel GpoApply | Out-Null
    Set-GPPermissions -Name $Nome -TargetName (NomeGrupo 'S-1-5-11') -TargetType Group -PermissionLevel GpoRead -Replace | Out-Null
    $filtro = NomeGrupo "$domSid-515"
}
catch {
    $filtro = 'Utilizadores autenticados (não foi possível mudar: ' + $_.Exception.Message + ')'
}

# 5) Ligar à raiz do domínio (se ainda não estiver)
$ligada = (Get-GPInheritance -Target $dn).GpoLinks | Where-Object { $_.DisplayName -eq $Nome }
if (-not $ligada) { New-GPLink -Name $Nome -Target $dn -LinkEnabled Yes | Out-Null }

Write-Host ''
Write-Host '=== GPO pronta ===' -ForegroundColor Green
Write-Host "Nome              : $Nome"
Write-Host "Script de arranque: $Script"
Write-Host "Aplicada a        : $filtro"
Write-Host "Ligada a          : $dn"
Write-Host "Versão            : $versao"
