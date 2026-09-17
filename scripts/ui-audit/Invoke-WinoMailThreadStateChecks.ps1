#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($reply in $records | Where-Object scenario -eq REPLY-001) {
    $original=Find-MailAuditExact @($records | Where-Object scenario -eq SEND-001) @{subject=($reply.subject -replace '^Re: ','')}
    $account=$reply.account
    try {
        Select-MailAuditAccount $RunDirectory THREAD-002 $Window $account
        Select-MailAuditFolder $RunDirectory THREAD-002 $Window (($original.folder -split '/',2)[1])
        $thread=Get-MailAuditThread (Get-MailAuditElements $RunDirectory THREAD-002 $Window) $reply.subject
        if ($thread.expandState -eq 'collapsed') { Invoke-MailAuditUi $RunDirectory THREAD-002 $Window @('invoke',$thread.selector) | Out-Null }
        Open-MailAuditContext $RunDirectory THREAD-002 $Window $reply.subject
        $elements=Get-MailAuditElements $RunDirectory THREAD-002 $Window
        if (@($elements | Where-Object { $_.automationId -eq 'MailContextOperation_MarkAsRead' }).Count) {
            Invoke-MailAuditUi $RunDirectory THREAD-002 $Window @('invoke','MailContextOperation_MarkAsRead') | Out-Null
        } else { Invoke-MailAuditUi $RunDirectory THREAD-002 $Window @('send-keys','escape','--via','send-input') | Out-Null }
        foreach ($case in @(@('THREAD-002','MarkAsUnread','MarkAsRead','UnreadSubjectTextBlock'),@('THREAD-003','MarkAsRead','MarkAsUnread','ReadSubjectTextBlock'),@('THREAD-004','MarkAsUnread','MarkAsRead','UnreadSubjectTextBlock'),@('THREAD-005','SetFlag','ClearFlag','flag'),@('THREAD-006','ClearFlag','SetFlag','flag'))) {
            try {
                Test-MailAuditContextTransition $RunDirectory $case[0] $Window $account $reply.subject ('MailContextOperation_'+$case[1]) ('MailContextOperation_'+$case[2])
                foreach ($fixture in @($original,$reply)) {
                    $elements=Get-MailAuditElements $RunDirectory $case[0] $Window
                    $rows=@($elements | Where-Object { $_.type -eq 'ListItem' -and -not $_.PSObject.Properties['expandState'] -and $_.PSObject.Properties['children'] })
                    $matching=@($rows | Where-Object { @(Expand-MailAuditElements $_.children | Where-Object { $_.name -eq $fixture.subject }).Count -gt 0 })
                    if ($matching.Count -ne 1) { throw 'Expected one expanded member.' }
                    if ($case[3] -ne 'flag') {
                        $null=Find-MailAuditExact @(Expand-MailAuditElements $matching[0].children) @{name=$fixture.subject;automationId=$case[3]}
                    } else {
                        $search=Find-MailAuditExact $elements @{type='Edit';name='Search';automationId='TextBox'}
                        Invoke-MailAuditUi $RunDirectory $case[0] $Window @('focus',$search.selector) | Out-Null
                        Invoke-MailAuditUi $RunDirectory $case[0] $Window @('click',$matching[0].selector,'--right') | Out-Null
                        Invoke-MailAuditUi $RunDirectory $case[0] $Window @('wait-for',('MailContextOperation_'+$case[2]),'--timeout','10000') | Out-Null
                        Invoke-MailAuditUi $RunDirectory $case[0] $Window @('send-keys','escape','--via','send-input') | Out-Null
                    }
                }
                Add-MailAuditResult $RunDirectory $case[0] $account PASS 'Aggregate and both members updated' "Thread context and both expanded members reflect $($case[1]). HWND $Window; Light." @('commands.jsonl',"evidence/$account-$($case[0]).png")
            } catch { Add-MailAuditResult $RunDirectory $case[0] $account FAIL 'Aggregate and both members updated' "$_" @('commands.jsonl') }
        }
        Write-Output "Thread state checks finished: $account"
    } catch { Write-Output "Thread setup blocked: $account : $_"; Add-MailAuditResult $RunDirectory THREAD-002 $account BLOCKED 'Known expanded thread' "$_" @('commands.jsonl') }
}
