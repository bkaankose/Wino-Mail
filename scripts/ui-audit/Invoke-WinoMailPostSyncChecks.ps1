#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($account in $config.accounts) {
    $fixture=Find-MailAuditExact @($records | Where-Object { $_.purpose -eq 'move' -and $_.state -eq 'received' }) @{recipient=$account.address}
    $folder=($fixture.folder -split '/',2)[1]
    Select-MailAuditAccount $RunDirectory READ-003 $Window $account.name
    Select-MailAuditFolder $RunDirectory READ-003 $Window $folder
    foreach ($case in @(@('READ-003','MarkAsUnread','MarkAsRead'),@('FLAG-001','SetFlag','ClearFlag'),@('FLAG-002','ClearFlag','SetFlag'))) {
        $scenario=$case[0]
        try {
            Open-MailAuditContext $RunDirectory $scenario $Window $fixture.subject
            $elements=Get-MailAuditElements $RunDirectory $scenario $Window
            $operation='MailContextOperation_'+$case[1]; $inverse='MailContextOperation_'+$case[2]
            if (@($elements | Where-Object automationId -eq $operation).Count) {
                @{fixture=$fixture.id;scenario=$scenario;action=$operation;state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('invoke',$operation) | Out-Null
            } else { Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','escape','--via','send-input') | Out-Null }
            $sent=Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory $scenario $Window) Sent
            Select-MailAuditFolder $RunDirectory $scenario $Window $sent
            Select-MailAuditFolder $RunDirectory $scenario $Window $folder
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('invoke','ShellSynchronizationButton') | Out-Null
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for',"Syncing $($account.name)",'--gone','--timeout','120000') | Out-Null
            Open-MailAuditContext $RunDirectory $scenario $Window $fixture.subject
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for',$inverse,'--timeout','10000') | Out-Null
            if ($scenario -eq 'READ-003') {
                $elements=Get-MailAuditElements $RunDirectory $scenario $Window
                $null=Find-MailAuditExact $elements @{name=$fixture.subject;automationId='UnreadSubjectTextBlock';type='Text'}
            }
            $path="evidence/$($account.name)-$scenario-PostSync.png"
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','escape','--via','send-input') | Out-Null
            Add-MailAuditResult $RunDirectory $scenario $account.name PASS 'State persists after navigation and synchronization' "Fixture $($fixture.id) retained $($case[1]) after folder roundtrip and sync request. HWND $Window; Light." @('commands.jsonl',$path) -Stage PostSync
        } catch { Add-MailAuditResult $RunDirectory $scenario $account.name FAIL 'State after synchronization' "$_" @('commands.jsonl') -Stage PostSync; throw }
    }
    Open-MailAuditContext $RunDirectory PERSIST-001 $Window $fixture.subject
    @{fixture=$fixture.id;scenario='PERSIST-001';action='SetFlag';state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
    Invoke-MailAuditUi $RunDirectory PERSIST-001 $Window @('invoke','MailContextOperation_SetFlag') | Out-Null
    Open-MailAuditContext $RunDirectory PERSIST-001 $Window $fixture.subject
    Invoke-MailAuditUi $RunDirectory PERSIST-001 $Window @('wait-for','MailContextOperation_ClearFlag','--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory PERSIST-001 $Window @('send-keys','escape','--via','send-input') | Out-Null
    @{account=$account.name;fixture=$fixture.id;subject=$fixture.subject;folder=$folder;unread=$true;flagged=$true;time=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json -Compress | Add-Content "$RunDirectory/retained-state.jsonl"
    Write-Output "Post-sync state checked: $($account.name)"
}
