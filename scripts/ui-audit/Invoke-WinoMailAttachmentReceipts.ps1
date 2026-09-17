#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$fixtures=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json | Where-Object scenario -eq 'DRAFT-001')
foreach ($fixture in $fixtures) {
    try {
        $destination=Find-MailAuditExact @($config.accounts) @{address=$fixture.recipient}
        foreach ($side in @('Sent','Received')) {
            $account=if ($side -eq 'Sent') { $fixture.account } else { $destination.name }
            Select-MailAuditAccount $RunDirectory ATTACH-001 $Window $account
            Invoke-MailAuditUi $RunDirectory ATTACH-001 $Window @('invoke','ShellSynchronizationButton') | Out-Null
            $roles=if ($side -eq 'Sent') { @('Sent') } else { @('Inbox','Junk') }
            $found=$false
            $timer=[Diagnostics.Stopwatch]::StartNew()
            do {
                foreach ($role in $roles) {
                    $folder=Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory ATTACH-001 $Window) $role
                    Select-MailAuditFolder $RunDirectory ATTACH-001 $Window $folder
                    $elements=Get-MailAuditElements $RunDirectory ATTACH-001 $Window
                    $rows=@($elements | Where-Object { $_.name -eq $fixture.subject -and $_.automationId -in @('ReadSubjectTextBlock','UnreadSubjectTextBlock') })
                    if ($rows.Count -eq 1) { $found=$true; break }
                }
                if (-not $found) { Start-Sleep -Seconds 2 }
            } until ($found -or $timer.Elapsed.TotalSeconds -ge 120)
            if (-not $found) { throw "No exact $side fixture within 120 seconds. Do not resend." }
            Invoke-MailAuditUi $RunDirectory ATTACH-001 $Window @('invoke',$rows[0].selector) | Out-Null
            Invoke-MailAuditUi $RunDirectory ATTACH-001 $Window @('wait-for','Vesika.png','--timeout','10000') | Out-Null
            $elements=Get-MailAuditElements $RunDirectory ATTACH-001 $Window
            if (-not @($elements | Where-Object { $_.name -like "*$($fixture.body)*" }).Count) { throw 'Edited body token missing.' }
            $path="evidence/$($fixture.account)-attachment-$side.png"
            Invoke-MailAuditUi $RunDirectory ATTACH-001 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
            if ($side -eq 'Received') {
                Set-MailAuditFixture $RunDirectory $fixture.id received "$account/$folder" @('commands.jsonl',$path)
                foreach ($scenario in @('DRAFT-001','ATTACH-001')) {
                    Add-MailAuditResult $RunDirectory $scenario $fixture.account PASS 'Sent and received edited draft with attachment' "Exact subject, edited body token and Vesika.png verified in sender Sent and $account/$folder. HWND $Window; Light theme." @('commands.jsonl',"evidence/$($fixture.account)-attachment-Sent.png",$path) -Stage CrossAccount
                }
            }
        }
    } catch {
        foreach ($scenario in @('DRAFT-001','ATTACH-001')) { Add-MailAuditResult $RunDirectory $scenario $fixture.account FAIL 'Cross-account draft and attachment receipt' "$_" @('commands.jsonl') -Stage CrossAccount }
    }
}
