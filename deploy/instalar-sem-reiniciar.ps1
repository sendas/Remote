#Requires -RunAsAdministrator
<#
  Acrescenta à GPO "Sala de Aula - Agente" uma tarefa imediata (Preferências de Política de Grupo):
  em cada atualização de política (a cada ~90 minutos, ou com "gpupdate /force") o PC corre o
  instalar.cmd como SYSTEM. Assim os PCs das salas ativas instalam/atualizam o agente SEM reiniciar,
  mesmo quando a firewall não deixa o servidor chegar a eles. Nos PCs já instalados não faz nada.

    .\instalar-sem-reiniciar.ps1
#>
param(
    [string]$Nome = 'Sala de Aula - Agente',
    [string]$Script = "\\$($env:COMPUTERNAME)\SalaDeAula$\instalar.cmd"
)
$ErrorActionPreference = 'Stop'
$dn = [string](([ADSI]'LDAP://RootDSE').defaultNamingContext)
$dominio = (Get-WmiObject Win32_ComputerSystem).Domain

# encontrar a GPO pelo nome (sem precisar do módulo GroupPolicy)
$s = New-Object DirectoryServices.DirectorySearcher([ADSI]"LDAP://CN=Policies,CN=System,$dn", "(&(objectClass=groupPolicyContainer)(displayName=$Nome))")
$r = $s.FindOne()
if (-not $r) { throw "Não encontro a GPO '$Nome'. Corre primeiro .\criar-gpo.ps1" }
$ad = $r.GetDirectoryEntry()
$guid = [string]$ad.Properties['cn'][0]
$sysvol = "\\$dominio\SYSVOL\$dominio\Policies\$guid"

$dir = "$sysvol\Machine\Preferences\ScheduledTasks"
New-Item $dir -ItemType Directory -Force | Out-Null
$agora = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<ScheduledTasks clsid="{CC63F200-7309-4ba0-B154-A71CD118DBCC}">
  <ImmediateTaskV2 clsid="{9756B581-76EC-4169-9AFC-0CA8D43ADB5F}" name="SalaViva - Instalar" image="0" changed="$agora" uid="{6D1B0C52-6F7A-4E43-9B53-5A1A57A1C0DE}">
    <Properties action="C" name="SalaViva - Instalar" runAs="NT AUTHORITY\System" logonType="S4U">
      <Task version="1.3">
        <RegistrationInfo><Author>SalaViva</Author><Description>Instala ou atualiza o agente SalaViva (só em salas ativas).</Description></RegistrationInfo>
        <Principals><Principal id="Author"><UserId>NT AUTHORITY\System</UserId><LogonType>S4U</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
        <Settings>
          <IdleSettings><Duration>PT5M</Duration><WaitTimeout>PT1H</WaitTimeout><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
          <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
          <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
          <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
          <AllowHardTerminate>true</AllowHardTerminate>
          <AllowStartOnDemand>true</AllowStartOnDemand>
          <Enabled>true</Enabled>
          <Hidden>true</Hidden>
          <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
          <Priority>7</Priority>
          <DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>
        </Settings>
        <Triggers><TimeTrigger><StartBoundary>%LocalTimeXmlEx%</StartBoundary><EndBoundary>%LocalTimeXmlEx%</EndBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
        <Actions Context="Author"><Exec><Command>cmd.exe</Command><Arguments>/c "$Script"</Arguments></Exec></Actions>
      </Task>
    </Properties>
  </ImmediateTaskV2>
</ScheduledTasks>
"@
[IO.File]::WriteAllText("$dir\ScheduledTasks.xml", $xml, (New-Object Text.UTF8Encoding $false))

# registar a extensão "Tarefas agendadas" (mantendo registo + scripts) e subir a versão
$ext = '[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]' +
       '[{42B5FAAE-6536-11D2-AE5A-0000F87571E3}{40B6664F-4972-11D1-A7CA-0000F87571E3}]' +
       '[{AADCED64-746C-4633-A97C-D61349046527}{CAB54552-DEEA-4691-817E-ED4A4D1AFC72}]'
$ad.Put('gPCMachineExtensionNames', $ext)
$versao = [int]$ad.Properties['versionNumber'][0] + 1
$ad.Put('versionNumber', $versao)
$ad.SetInfo()
(Get-Content "$sysvol\GPT.INI") -replace '^Version=\d+', "Version=$versao" | Set-Content "$sysvol\GPT.INI" -Encoding ASCII

Write-Host ''
Write-Host '=== Instalação sem reiniciar ativada ===' -ForegroundColor Green
Write-Host "GPO     : $Nome ($guid), versão $versao"
Write-Host "Corre   : $Script (como SYSTEM, a cada atualização de política)"
Write-Host 'Os PCs aplicam em até ~90 minutos. Num PC, para ser já: gpupdate /force'
