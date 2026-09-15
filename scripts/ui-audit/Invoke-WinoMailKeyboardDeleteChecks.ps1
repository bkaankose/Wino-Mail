#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[string[]]$Accounts=@())
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
foreach ($account in $config.accounts) {
    if ($Accounts.Count -and $account.name -notin $Accounts) { continue }
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    $fixture=Find-MailAuditExact @($records | Where-Object { $_.purpose -eq 'keyboard' -and $_.state -eq 'received' }) @{recipient=$account.address}
    $folder=($fixture.folder -split '/',2)[1]
    try {
        Select-MailAuditAccount $RunDirectory KEY-009 $Window $account.name
        Select-MailAuditFolder $RunDirectory KEY-009 $Window $folder
        $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory KEY-009 $Window) $fixture.subject
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('invoke',$row.selector) | Out-Null
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('focus',$row.selector) | Out-Null
        @{fixture=$fixture.id;account=$account.name;action='Delete key';state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('send-keys','delete','--via','send-input') | Out-Null
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('wait-for',$fixture.subject,'--gone','--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('invoke','ShellSynchronizationButton') | Out-Null
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('wait-for',"Syncing $($account.name)",'--gone','--timeout','120000') | Out-Null
        $trash=Wait-MailAuditFolderRole $RunDirectory KEY-009 $Window Trash
        Select-MailAuditFolder $RunDirectory KEY-009 $Window $trash
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('wait-for',$fixture.subject,'--timeout','120000') | Out-Null
        $path="evidence/$($account.name)-key-delete.png"
        Invoke-MailAuditUi $RunDirectory KEY-009 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
        Set-MailAuditFixture $RunDirectory $fixture.id deleted "$($account.name)/$trash" @('commands.jsonl',$path)
        Add-MailAuditResult $RunDirectory KEY-009 $account.name PASS 'Delete shortcut moves only the dedicated fixture to Trash' "Delete key removed exact fixture from $folder within 10 seconds. Verified in $trash after synchronization. HWND $Window; Light." @('commands.jsonl',$path)
    } catch {
        Add-MailAuditResult $RunDirectory KEY-009 $account.name FAIL 'Delete shortcut moves dedicated fixture to Trash' "$_ Reconcile before another deletion." @('commands.jsonl')
        throw
    }
    Write-Output "Keyboard deletion checked: $($account.name)"
}
