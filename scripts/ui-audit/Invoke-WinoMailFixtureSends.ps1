#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[Parameter(Mandatory)][string[]]$Accounts,[string[]]$Purposes=@('keyboard','single-delete','thread-delete','bulk-one','bulk-two','move'))
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
foreach ($name in $Accounts) {
    $account=Find-MailAuditExact @($config.accounts) @{name=$name}
    foreach ($purpose in $Purposes) {
        $records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
        if (@($records | Where-Object { $_.account -eq $name -and $_.purpose -eq $purpose }).Count) { Write-Output "Existing $name/$purpose skipped; reconcile separately."; continue }
        $scenario=switch ($purpose) { keyboard {'KEY-001'} single-delete {'DELETE-001'} thread-delete {'DELETE-002'} move {'MOVE-001'} default {'BULK-001'} }
        $fixture=Add-MailAuditFixture $RunDirectory $name $scenario $account.recipient $purpose
        try {
            New-MailAuditCompose $RunDirectory $scenario $Window $fixture -UseShortcut:($purpose -eq 'keyboard')
            if ($purpose -eq 'keyboard') {
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('focus','SubjectTextBox') | Out-Null
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','end','--via','send-input') | Out-Null
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','x','--verbatim','--via','send-input') | Out-Null
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('send-keys','backspace','--via','send-input') | Out-Null
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','SubjectTextBox','--value',$fixture.subject,'--timeout','10000') | Out-Null
                $path="evidence/$name-key-new.png"
                Invoke-MailAuditUi $RunDirectory $scenario $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
                Add-MailAuditResult $RunDirectory KEY-001 $name PASS 'New shortcut and composer editing' "Ctrl+N opened composer; From/To/subject/body verified. Subject typing and backspace stayed in composer. HWND $Window; Light theme." @('commands.jsonl',$path)
            }
            Set-MailAuditFixture $RunDirectory $fixture.id send-intended Drafts @()
            if ($purpose -eq 'keyboard') { Invoke-MailAuditUi $RunDirectory KEY-008 $Window @('send-keys','ctrl+enter','--via','send-input') | Out-Null }
            else { Invoke-MailAuditUi $RunDirectory $scenario $Window @('invoke','ComposePageAppBarButton4') | Out-Null }
            Invoke-MailAuditUi $RunDirectory $scenario $Window @('wait-for','SubjectTextBox','--gone','--timeout','10000') | Out-Null
            Write-Output "Dispatched once: $name/$purpose. Receipt is unverified."
        } catch { throw "Reconcile $name/$purpose before continuing. $_" }
    }
}
