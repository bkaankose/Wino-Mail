#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$originals=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json | Where-Object { $_.purpose -eq 'forward' -and $_.state -eq 'received' })
foreach ($original in $originals) {
    $account=Find-MailAuditExact @($config.accounts) @{address=$original.recipient}
    $sender=Find-MailAuditExact @($config.accounts) @{name=$original.account}
    $recipients=@($sender.address)+@($original.recipients | Where-Object { $_ -ne $account.address })
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    if (@($records | Where-Object { $_.purpose -eq 'reply-all' -and $_.account -eq $account.name }).Count) { throw 'Existing reply-all requires reconciliation.' }
    $fixture=Add-MailAuditFixture $RunDirectory $account.name REPLYALL-001 $sender.address reply-all
    Select-MailAuditAccount $RunDirectory KEY-007 $Window $account.name
    Select-MailAuditFolder $RunDirectory KEY-007 $Window (($original.folder -split '/',2)[1])
    $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory KEY-007 $Window) $original.subject
    Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('invoke',$row.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('focus',$row.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('send-keys','ctrl+shift+r','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('wait-for','AccountsComboBox','--value',$account.address,'--timeout','10000') | Out-Null
    $subject=(Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('get-value','SubjectTextBox') | ConvertFrom-Json).text
    if (($subject -replace '^(?i:re):\s*','') -cne $original.subject) { throw 'Unexpected reply-all subject.' }
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    $fixture=Find-MailAuditExact $records @{id=$fixture.id}; $fixture.subject=$subject
    $fixture | Add-Member parentId $original.id
    $fixture | Add-Member recipients $recipients
    ConvertTo-Json -InputObject $records -Depth 10 | Set-Content "$RunDirectory/records.json"
    Assert-MailAuditRecipients $RunDirectory KEY-007 $Window $recipients
    Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('wait-for','wino-editor','--value',$original.body,'--contains','--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('focus','SubjectTextBox') | Out-Null
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('click','wino-editor') | Out-Null
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('send-keys','ctrl+home','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('send-keys',$fixture.body,'--verbatim','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('wait-for','wino-editor','--value',$fixture.body,'--contains','--timeout','10000') | Out-Null
    Assert-MailAuditRecipients $RunDirectory REPLYALL-001 $Window $recipients
    $path="evidence/$($account.name)-reply-all-draft.png"
    Invoke-MailAuditUi $RunDirectory KEY-007 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
    Add-MailAuditResult $RunDirectory KEY-007 $account.name PASS 'Reply-all shortcut includes configured participants' 'Ctrl+Shift+R opened composer with the original sender and third participant, excluding self. From, subject, quote and added token verified.' @('commands.jsonl',$path)
    Set-MailAuditFixture $RunDirectory $fixture.id draft Drafts @('commands.jsonl',$path)
    Set-MailAuditFixture $RunDirectory $fixture.id send-intended Drafts @()
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('invoke','ComposePageAppBarButton4') | Out-Null
    Invoke-MailAuditUi $RunDirectory REPLYALL-001 $Window @('wait-for','SubjectTextBox','--gone','--timeout','10000') | Out-Null
    Write-Output "Reply-all sent once: $($account.name)"
}
