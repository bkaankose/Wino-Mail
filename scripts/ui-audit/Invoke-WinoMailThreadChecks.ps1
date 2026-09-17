#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($reply in $records | Where-Object scenario -eq REPLY-001) {
    $original=Find-MailAuditExact @($records | Where-Object scenario -eq SEND-001) @{subject=($reply.subject -replace '^Re: ','')}
    foreach ($side in @('replier','original-sender')) {
        $account=if ($side -eq 'replier') { $reply.account } else { $original.account }
        try {
            Select-MailAuditAccount $RunDirectory REPLY-001 $Window $account
            $elements=Get-MailAuditElements $RunDirectory REPLY-001 $Window
            $folder=if ($side -eq 'replier') { ($original.folder -split '/',2)[1] } else { Resolve-MailAuditFolder $elements Sent }
            Select-MailAuditFolder $RunDirectory REPLY-001 $Window $folder
            Invoke-MailAuditUi $RunDirectory REPLY-001 $Window @('invoke','ShellSynchronizationButton') | Out-Null
            $elements=Get-MailAuditElements $RunDirectory REPLY-001 $Window
            $thread=Get-MailAuditThread $elements $reply.subject
            if ($thread.expandState -eq 'collapsed') { Invoke-MailAuditUi $RunDirectory THREAD-001 $Window @('invoke',$thread.selector) | Out-Null }
            $elements=Get-MailAuditElements $RunDirectory THREAD-001 $Window
            $thread=Get-MailAuditThread $elements $reply.subject
            if ($thread.expandState -ne 'expanded') { throw 'Thread did not expand.' }
            $list=Find-MailAuditExact $elements @{type='List';automationId='MailListView'}
            $rows=@(Expand-MailAuditElements $list.children | Where-Object { $_.type -eq 'ListItem' -and -not $_.PSObject.Properties['expandState'] })
            foreach ($fixture in @($original,$reply)) {
                $matching=@($rows | Where-Object { @(Expand-MailAuditElements $_.children | Where-Object { $_.name -eq $fixture.subject }).Count -gt 0 })
                if ($matching.Count -ne 1) { throw "Expected one member for $($fixture.subject); found $($matching.Count)." }
                if (-not @(Expand-MailAuditElements $matching[0].children | Where-Object { $_.name -like "*$($fixture.id)*" }).Count) { throw "Member token absent: $($fixture.id)" }
            }
            $path="evidence/$($reply.account)-thread-$side.png"
            Invoke-MailAuditUi $RunDirectory THREAD-001 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
            Invoke-MailAuditUi $RunDirectory THREAD-001 $Window @('invoke',$thread.selector) | Out-Null
            $collapsed=Get-MailAuditThread (Get-MailAuditElements $RunDirectory THREAD-001 $Window) $reply.subject
            if ($collapsed.expandState -ne 'collapsed') { throw 'Thread did not collapse.' }
            Add-MailAuditResult $RunDirectory REPLY-001 $reply.account PASS 'Two-member conversation in both accounts' "$side side verified in $account/${folder}: two distinct original/reply rows with body tokens. HWND $Window; Light." @('commands.jsonl',$path) -Stage $side
            if ($side -eq 'replier') { Add-MailAuditResult $RunDirectory THREAD-001 $account PASS 'Expand and collapse known conversation' 'Two distinct members and tokens verified; expanded then collapsed.' @('commands.jsonl',$path) }
            Write-Output "$($reply.account) $side thread PASS"
        } catch {
            Add-MailAuditResult $RunDirectory REPLY-001 $reply.account FAIL 'Two-member conversation' "$side side in $account : $_" @('commands.jsonl') -Stage $side
            Write-Output "$($reply.account) $side thread FAIL: $_"
        }
    }
}
