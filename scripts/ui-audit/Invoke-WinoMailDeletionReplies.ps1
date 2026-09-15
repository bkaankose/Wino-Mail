#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$originals=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json | Where-Object { $_.purpose -eq 'thread-delete' -and $_.state -eq 'received' })
foreach ($original in $originals) {
    $account=Find-MailAuditExact @($config.accounts) @{address=$original.recipient}
    $sender=Find-MailAuditExact @($config.accounts) @{name=$original.account}
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    if (@($records | Where-Object { $_.purpose -eq 'thread-delete-reply' -and $_.account -eq $account.name }).Count) { Write-Output "Existing deletion reply for $($account.name); reconcile separately."; continue }
    Select-MailAuditAccount $RunDirectory DELETE-002 $Window $account.name
    Select-MailAuditFolder $RunDirectory DELETE-002 $Window (($original.folder -split '/',2)[1])
    $fixture=Add-MailAuditFixture $RunDirectory $account.name DELETE-002 $sender.address thread-delete-reply
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    $fixture=Find-MailAuditExact $records @{id=$fixture.id}
    $fixture.subject='Re: '+$original.subject
    $fixture | Add-Member parentId $original.id
    ConvertTo-Json -InputObject $records -Depth 10 | Set-Content "$RunDirectory/records.json"
    $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory KEY-006 $Window) $original.subject
    Invoke-MailAuditUi $RunDirectory KEY-006 $Window @('invoke',$row.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory KEY-006 $Window @('focus',$row.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory KEY-006 $Window @('send-keys','ctrl+r','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory KEY-006 $Window @('wait-for','wino-editor','--value',$original.body,'--contains','--timeout','10000') | Out-Null
    $path="evidence/$($account.name)-key-reply.png"
    Invoke-MailAuditUi $RunDirectory KEY-006 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
    Complete-MailAuditReply $RunDirectory DELETE-002 $Window $fixture
    Add-MailAuditResult $RunDirectory KEY-006 $account.name PASS 'Reply shortcut targets sender and quotes original' 'Ctrl+R opened reply. Sender, recipient, subject, original quote and added token verified before one send.' @('commands.jsonl',$path)
    Invoke-MailAuditUi $RunDirectory DELETE-002 $Window @('wait-for','SubjectTextBox','--gone','--timeout','10000') | Out-Null
    Write-Output "Deletion conversation reply dispatched once: $($account.name)"
}
