#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[string[]]$Purposes=@('keyboard','single-delete','thread-delete','bulk-one','bulk-two','move'))
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$fixtures=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json | Where-Object { $_.purpose -in $Purposes -and $_.state -in @('send-intended','sent') })
foreach ($fixture in $fixtures) {
    $destination=Find-MailAuditExact @($config.accounts) @{address=$fixture.recipient}
    $sender=Find-MailAuditExact @($config.accounts) @{name=$fixture.account}
    try {
        $targets=@([pscustomobject]@{side='Sent';account=$fixture.account})
        $recipients=if ($fixture.PSObject.Properties['recipients']) { @($fixture.recipients) } else { @($fixture.recipient) }
        foreach ($address in $recipients) { $target=Find-MailAuditExact @($config.accounts) @{address=$address}; $targets += [pscustomobject]@{side='Received';account=$target.name} }
        $evidence=@('commands.jsonl'); $primaryFolder=$null
        foreach ($target in $targets) {
            $side=$target.side; $account=$target.account
            Select-MailAuditAccount $RunDirectory $fixture.scenario $Window $account
            Invoke-MailAuditUi $RunDirectory $fixture.scenario $Window @('invoke','ShellSynchronizationButton') | Out-Null
            $roles=if ($side -eq 'Sent') { @('Sent') } else { @('Inbox','Junk') }
            $timer=[Diagnostics.Stopwatch]::StartNew(); $found=$false
            do {
                foreach ($role in $roles) {
                    $folder=Wait-MailAuditFolderRole $RunDirectory $fixture.scenario $Window $role
                    Select-MailAuditFolder $RunDirectory $fixture.scenario $Window $folder
                    $elements=Get-MailAuditElements $RunDirectory $fixture.scenario $Window
                    $rows=@($elements | Where-Object { $_.name -eq $fixture.subject -and $_.automationId -in @('ReadSubjectTextBlock','UnreadSubjectTextBlock') })
                    if ($rows.Count -eq 1) { $found=$true; break }
                }
                if (-not $found) { Start-Sleep -Seconds 2 }
            } until ($found -or $timer.Elapsed.TotalSeconds -ge 120)
            if (-not $found) { throw "Exact fixture missing from $side after bounded polling. Never resend." }
            Invoke-MailAuditUi $RunDirectory $fixture.scenario $Window @('invoke',$rows[0].selector) | Out-Null
            Assert-MailAuditReader $RunDirectory $fixture.scenario $Window $fixture $sender.address
            if ($fixture.PSObject.Properties['parentId']) {
                $parent=Find-MailAuditExact @(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json) @{id=$fixture.parentId}
                Assert-MailAuditReader $RunDirectory $fixture.scenario $Window ([pscustomobject]@{subject=$fixture.subject;body=$parent.body}) $sender.address
            }
            $path="evidence/$($fixture.account)-$($fixture.purpose)-$side-$account.png"
            Invoke-MailAuditUi $RunDirectory $fixture.scenario $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
            $evidence += $path
            @{fixture=$fixture.id;side=$side;account=$account;folder=$folder;bodyVerified=$true;evidence=$path;time=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json -Compress | Add-Content "$RunDirectory/fixture-observations.jsonl"
            if ($side -eq 'Received' -and $account -eq $destination.name) { $primaryFolder="$account/$folder" }
        }
        Set-MailAuditFixture $RunDirectory $fixture.id received $primaryFolder $evidence
        $resultAccount=if ($fixture.purpose -in @('keyboard','forward','reply-all')) { $fixture.account } else { $destination.name }
        Add-MailAuditResult $RunDirectory $fixture.scenario $resultAccount PASS 'Verified setup fixture' "Fixture $($fixture.id) verified in Sent and all $($recipients.Count) configured recipients. Primary: $primaryFolder." $evidence -Stage Setup
        if ($fixture.purpose -eq 'keyboard') { Add-MailAuditResult $RunDirectory KEY-008 $fixture.account PASS 'Send shortcut creates Sent and received copies' "Ctrl+Enter dispatched once. Exact Sent and received copies contain token and sender. HWND $Window; Light theme." $evidence }
        if ($fixture.purpose -in @('forward','reply-all')) { Add-MailAuditResult $RunDirectory $fixture.scenario $fixture.account PASS 'Correct recipients receive original content and new token' "Sent and all $($recipients.Count) recipient copies verified with sender, new body token and quoted parent token. HWND $Window; Light theme." $evidence }
        Write-Output "Verified: $($fixture.account)/$($fixture.purpose)"
    } catch {
        $resultAccount=if ($fixture.purpose -in @('keyboard','forward','reply-all')) { $fixture.account } else { $destination.name }
        Add-MailAuditResult $RunDirectory $fixture.scenario $resultAccount BLOCKED 'Verified setup fixture' "$_" @('commands.jsonl') -Stage Setup
        Write-Output "Blocked setup: $($fixture.account)/$($fixture.purpose): $_"
    }
}
