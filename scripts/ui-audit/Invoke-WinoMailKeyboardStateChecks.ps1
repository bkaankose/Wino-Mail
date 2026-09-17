#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[string[]]$Accounts=@())
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($account in $config.accounts) {
    if ($Accounts.Count -and $account.name -notin $Accounts) { continue }
    $fixture=Find-MailAuditExact @($records | Where-Object { $_.purpose -eq 'keyboard' -and $_.state -eq 'received' }) @{recipient=$account.address}
    Select-MailAuditAccount $RunDirectory KEY-002 $Window $account.name
    Select-MailAuditFolder $RunDirectory KEY-002 $Window (($fixture.folder -split '/',2)[1])
    foreach ($case in @(@('KEY-002','ctrl+u','MarkAsRead','MarkAsUnread'),@('KEY-003','ctrl+shift+g','ClearFlag','SetFlag'))) {
        $scenario=$case[0]
        try {
            $evidence=@('commands.jsonl')
            for ($direction=0;$direction -lt 2;$direction++) {
                Open-MailAuditContext $RunDirectory $scenario $Window $fixture.subject
                $elements=Get-MailAuditElements $RunDirectory $scenario $Window
                $current=@($elements | Where-Object { $_.automationId -in @("MailContextOperation_$($case[2])","MailContextOperation_$($case[3])") })
                if ($current.Count -ne 1) { throw 'Ambiguous state before shortcut.' }
                $expected=if ($current[0].automationId -eq "MailContextOperation_$($case[2])") { "MailContextOperation_$($case[3])" } else { "MailContextOperation_$($case[2])" }
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','escape','--via','send-input') | Out-Null
                $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory $scenario $Window) $fixture.subject
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('focus',$row.selector) | Out-Null
                @{fixture=$fixture.id;scenario=$scenario;action=$case[1];before=$current[0].automationId;expected=$expected;state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys',$case[1],'--via','send-input') | Out-Null
                Open-MailAuditContext $RunDirectory $scenario $Window $fixture.subject
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for',$expected,'--timeout','10000') | Out-Null
                if ($scenario -eq 'KEY-002') {
                    $id=if ($expected -eq 'MailContextOperation_MarkAsRead') { 'UnreadSubjectTextBlock' } else { 'ReadSubjectTextBlock' }
                    $null=Find-MailAuditExact (Get-MailAuditElements $RunDirectory $scenario $Window) @{name=$fixture.subject;automationId=$id}
                }
                $path="evidence/$($account.name)-$scenario-$direction.png"
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
                $evidence+=$path
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','escape','--via','send-input') | Out-Null
            }
            Add-MailAuditResult $RunDirectory $scenario $account.name PASS 'Shortcut changes target state in both directions' "$($case[1]) toggled exact fixture $($fixture.id) twice; inverse context labels verified. Read row style also checked. HWND $Window; Light." $evidence
        } catch {
            Add-MailAuditResult $RunDirectory $scenario $account.name FAIL 'Shortcut changes target state in both directions' "$_" @('commands.jsonl')
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','escape','--via','send-input') | Out-Null
        }
    }
    Write-Output "Keyboard state checks finished: $($account.name)"
}
