#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[Parameter(Mandatory)][string[]]$Accounts)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
foreach ($name in $Accounts) {
    $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    if (@($records | Where-Object { $_.account -eq $name -and $_.scenario -eq 'DRAFT-002' }).Count) { throw "Existing $name discard fixture requires reconciliation." }
    $account=Find-MailAuditExact @($config.accounts) @{name=$name}
    $fixture=Add-MailAuditFixture $RunDirectory $name DRAFT-002 $account.recipient discard
    Select-MailAuditAccount $RunDirectory DRAFT-002 $Window $name
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('invoke','XBindDomainTranslatorMenuNewMailModeOneTime') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('wait-for','AccountsComboBox','--value',$account.address,'--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('set-value','SubjectTextBox',$fixture.subject) | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('wait-for','SubjectTextBox','--value',$fixture.subject,'--timeout','10000') | Out-Null
    Set-MailAuditFixture $RunDirectory $fixture.id draft "$name/subject-only draft" @('commands.jsonl')
    @{scenario='DRAFT-002';fixture=$fixture.id;action='cancel discard, then confirm discard';state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('invoke','ComposePageAppBarButton3') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('wait-for','SecondaryButton','--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('invoke','SecondaryButton') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('wait-for','SubjectTextBox','--value',$fixture.subject,'--timeout','10000') | Out-Null
    $path="evidence/$name-discard-cancel.png"
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('invoke','ComposePageAppBarButton3') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('invoke','PrimaryButton') | Out-Null
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('wait-for','SubjectTextBox','--gone','--timeout','10000') | Out-Null
    Select-MailAuditFolder $RunDirectory DRAFT-002 $Window Inbox
    $drafts=Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory DRAFT-002 $Window) Drafts
    Select-MailAuditFolder $RunDirectory DRAFT-002 $Window $drafts
    Invoke-MailAuditUi $RunDirectory DRAFT-002 $Window @('wait-for',$fixture.subject,'--gone','--timeout','10000') | Out-Null
    Set-MailAuditFixture $RunDirectory $fixture.id deleted "Discarded; absent from $name/$drafts" @('commands.jsonl',$path)
    Add-MailAuditResult $RunDirectory DRAFT-002 $name PASS 'Cancel retains draft; Yes removes it' "Subject-only audit draft retained after Cancel, then absent after Yes and navigation. HWND $Window; Light theme." @('commands.jsonl',$path)
}
