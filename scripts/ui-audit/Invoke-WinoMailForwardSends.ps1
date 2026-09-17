#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[string[]]$Accounts=@())
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
foreach ($account in $config.accounts) {
    if ($Accounts.Count -and $account.name -notin $Accounts) { continue }
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    if (@($records | Where-Object { $_.purpose -eq 'forward' -and $_.account -eq $account.name }).Count) { throw 'Existing forward requires reconciliation.' }
    $original=Find-MailAuditExact @($records | Where-Object purpose -eq 'draft-attachment') @{recipient=$account.address}
    $destination=Find-MailAuditExact @($config.accounts) @{address=$account.recipient}
    $recipients=@($account.recipient,$destination.recipient)
    $fixture=Add-MailAuditFixture $RunDirectory $account.name FORWARD-001 $account.recipient forward
    Select-MailAuditAccount $RunDirectory FORWARD-001 $Window $account.name
    Select-MailAuditFolder $RunDirectory FORWARD-001 $Window (($original.folder -split '/',2)[1])
    Open-MailAuditContext $RunDirectory FORWARD-001 $Window $original.subject
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('invoke','MailContextHeaderForward') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('wait-for','AccountsComboBox','--value',$account.address,'--timeout','10000') | Out-Null
    $subject=(Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('get-value','SubjectTextBox') | ConvertFrom-Json).text
    if ($subject -notmatch '^(?i:fw|fwd):\s*' -or ($subject -replace '^(?i:fw|fwd):\s*','') -cne $original.subject) { throw 'Unexpected forward subject.' }
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    $fixture=Find-MailAuditExact $records @{id=$fixture.id}
    $fixture.subject=$subject
    $fixture | Add-Member parentId $original.id
    $fixture | Add-Member recipients $recipients
    ConvertTo-Json -InputObject $records -Depth 10 | Set-Content "$RunDirectory/records.json"
    foreach ($address in $recipients) { Add-MailAuditToRecipient $RunDirectory FORWARD-001 $Window $address }
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('wait-for','wino-editor','--value',$original.body,'--contains','--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('focus','SubjectTextBox') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('click','wino-editor') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('send-keys','ctrl+home','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('send-keys',$fixture.body,'--verbatim','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('wait-for','wino-editor','--value',$fixture.body,'--contains','--timeout','10000') | Out-Null
    Assert-MailAuditRecipients $RunDirectory FORWARD-001 $Window $recipients
    $path="evidence/$($account.name)-forward-draft.png"
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
    Set-MailAuditFixture $RunDirectory $fixture.id draft Drafts @('commands.jsonl',$path)
    Set-MailAuditFixture $RunDirectory $fixture.id send-intended Drafts @()
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('invoke','ComposePageAppBarButton4') | Out-Null
    Invoke-MailAuditUi $RunDirectory FORWARD-001 $Window @('wait-for','SubjectTextBox','--gone','--timeout','10000') | Out-Null
    Write-Output "Forward sent once: $($account.name); two configured recipients."
}
