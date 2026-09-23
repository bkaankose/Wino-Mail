#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[Parameter(Mandatory)][string[]]$Accounts)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
foreach ($name in $Accounts) {
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    if (@($records | Where-Object { $_.account -eq $name -and $_.scenario -eq 'DRAFT-001' }).Count) { throw "Draft fixture already exists for $name. Reconcile it before another attempt." }
    $account=Find-MailAuditExact @($config.accounts) @{name=$name}
    $fixture=Add-MailAuditFixture $RunDirectory $name DRAFT-001 $account.recipient draft-attachment
    $scenario='DRAFT-001'
    try {
        New-MailAuditCompose $RunDirectory $scenario $Window $fixture
        Select-MailAuditFolder $RunDirectory $scenario $Window Inbox
        $drafts=Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory $scenario $Window) Drafts
        Select-MailAuditFolder $RunDirectory $scenario $Window $drafts
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for',$fixture.subject,'--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('invoke',$fixture.subject) | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','SubjectTextBox','--value',$fixture.subject,'--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','wino-editor','--value',$fixture.body,'--contains','--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','AccountsComboBox','--value',$account.address,'--timeout','10000') | Out-Null
        $elements=Get-MailAuditElements $RunDirectory $scenario $Window
        $to=Find-MailAuditExact $elements @{type='List';automationId='ToBox'}
        $recipients=@($to.children | Where-Object { $_.type -eq 'ListItem' -and $_.name })
        $null=Find-MailAuditExact $recipients @{name=$fixture.recipient}
        if ($recipients.Count -ne 1) { throw 'Reopened draft has unexpected recipients.' }
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('focus','SubjectTextBox') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('click','wino-editor') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','ctrl+home','--via','send-input') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','Edited draft.','--verbatim','--via','send-input') | Out-Null
        $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
        $fixture=Find-MailAuditExact $records @{id=$fixture.id}
        $fixture.body='Edited draft.'+$fixture.body
        ConvertTo-Json -InputObject $records -Depth 10 | Set-Content "$RunDirectory/records.json"
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','wino-editor','--value',$fixture.body,'--contains','--timeout','10000') | Out-Null
        $scenario='ATTACH-001'
        Add-MailAuditAttachment $RunDirectory $scenario $Window
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('invoke','ComposeAttachmentMoreButton') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','ComposeAttachmentRemove','--timeout','5000') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('invoke','ComposeAttachmentRemove') | Out-Null
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for',([IO.Path]::GetFileName($config.attachmentPath)),'--gone','--timeout','10000') | Out-Null
        Add-MailAuditAttachment $RunDirectory $scenario $Window
        $path="evidence/$name-draft-attachment.png"
        Invoke-MailAuditUi $RunDirectory $scenario $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
        Set-MailAuditFixture $RunDirectory $fixture.id send-intended $drafts @()
        Invoke-MailAuditUi $RunDirectory DRAFT-001 $Window @('invoke','ComposePageAppBarButton4') | Out-Null
        Add-MailAuditResult $RunDirectory DRAFT-001 $name PASS 'Draft fields persist and edits are sent' 'Closed/reopened draft retained sender, recipient, subject and body. Edited body verified before one send. Receipt pending.' @('commands.jsonl',$path)
        Add-MailAuditResult $RunDirectory ATTACH-001 $name PASS 'Add, remove and re-add attachment' 'Image appeared, disappeared after removal, and appeared after re-add. Sent once; receipt pending.' @('commands.jsonl',$path)
        Write-Output "Draft and attachment sent once: $name"
    } catch {
        Add-MailAuditResult $RunDirectory $scenario $name FAIL 'Draft and attachment flow' "$_" @('commands.jsonl')
        throw "Reconcile the current draft or picker before continuing: $_"
    }
}
