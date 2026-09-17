#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[string[]]$Accounts=@())
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($reply in $records | Where-Object purpose -eq 'thread-delete-reply') {
    if (($Accounts.Count -and $reply.account -notin $Accounts) -or $reply.state -eq 'deleted') { continue }
    $original=Find-MailAuditExact $records @{id=$reply.parentId}
    $account=$reply.account; $folder=($original.folder -split '/',2)[1]
    try {
        Select-MailAuditAccount $RunDirectory DELETE-002 $Window $account
        Select-MailAuditFolder $RunDirectory DELETE-002 $Window $folder
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('invoke','ShellSynchronizationButton') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for',"Syncing $account",'--gone','--timeout','120000') | Out-Null
        $thread=Get-MailAuditThread (Get-MailAuditElements $RunDirectory DELETE-002 $Window) $reply.subject
        if ($thread.expandState -eq 'collapsed') { Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('invoke',$thread.selector) | Out-Null }
        foreach ($fixture in @($original,$reply)) {
            $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory DELETE-002 $Window) $fixture.subject
            if (-not @(Expand-MailAuditElements $row.children | Where-Object { $_.name -like "*$($fixture.id)*" }).Count) { throw 'Conversation member token missing before deletion.' }
        }
        $before="evidence/$account-thread-delete-before.png"
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('screenshot','-o',(Join-Path $RunDirectory $before)) | Out-Null
        @{scenario='DELETE-002';account=$account;fixtures=@($original.id,$reply.id);action='SoftDelete whole verified conversation';state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
        Open-MailAuditContext $RunDirectory DELETE-002 $Window $reply.subject
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('invoke','MailContextOperation_SoftDelete') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for',$reply.subject,'--gone','--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for',$original.subject,'--gone','--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('invoke','ShellSynchronizationButton') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for',"Syncing $account",'--gone','--timeout','120000') | Out-Null
        $sent=Wait-MailAuditFolderRole $RunDirectory DELETE-002 $Window Sent
        Select-MailAuditFolder $RunDirectory DELETE-002 $Window $sent
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for',$reply.subject,'--gone','--timeout','10000') | Out-Null
        $trash=Wait-MailAuditFolderRole $RunDirectory DELETE-002 $Window Trash
        Select-MailAuditFolder $RunDirectory DELETE-002 $Window $trash
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for',$reply.subject,'--timeout','120000') | Out-Null
        $elements=Get-MailAuditElements $RunDirectory DELETE-002 $Window
        $thread=Get-MailAuditThread $elements $reply.subject
        if ($thread.expandState -eq 'collapsed') { Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('invoke',$thread.selector) | Out-Null }
        foreach ($fixture in @($original,$reply)) {
            $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory DELETE-002 $Window) $fixture.subject
            if (-not @(Expand-MailAuditElements $row.children | Where-Object { $_.name -like "*$($fixture.id)*" }).Count) { throw 'Expected member missing from Trash.' }
        }
        $path="evidence/$account-thread-deleted.png"
        Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
        foreach ($fixture in @($original,$reply)) { Set-MailAuditFixture $RunDirectory $fixture.id deleted "$account/$trash" @('commands.jsonl',$before,$path) }
        Add-MailAuditResult $RunDirectory DELETE-002 $account PASS 'Whole conversation moves to Trash' "Original and reply tokens verified before deletion and in Trash. Removed from source $folder and reply absent from $sent after sync. HWND $Window; Light." @('commands.jsonl',$before,$path)
        Write-Output "Thread deletion checked: $account"
    } catch { Add-MailAuditResult $RunDirectory DELETE-002 $account FAIL 'Whole conversation deletion' "$_" @('commands.jsonl'); throw 'Reconcile the current conversation before continuing independent accounts.' }
}
