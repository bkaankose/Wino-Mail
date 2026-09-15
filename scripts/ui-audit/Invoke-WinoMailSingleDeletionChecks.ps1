#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[string[]]$Accounts=@())
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($account in $config.accounts) {
    if ($Accounts.Count -and $account.name -notin $Accounts) { continue }
    $fixture=Find-MailAuditExact @($records | Where-Object { $_.purpose -eq 'single-delete' -and $_.state -eq 'received' }) @{recipient=$account.address}
    $folder=($fixture.folder -split '/',2)[1]
    try {
        Select-MailAuditAccount $RunDirectory DELETE-001 $Window $account.name
        Select-MailAuditFolder $RunDirectory DELETE-001 $Window $folder
        Open-MailAuditContext $RunDirectory DELETE-001 $Window $fixture.subject
        @{fixture=$fixture.id;account=$account.name;action='SoftDelete';state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
        $timer=[Diagnostics.Stopwatch]::StartNew()
        Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('invoke','MailContextOperation_SoftDelete') | Out-Null
        $elements=Get-MailAuditElements $RunDirectory DELETE-001 $Window
        $cancel=@($elements | Where-Object { $_.automationId -eq 'SecondaryButton' -and $_.name -eq 'Cancel' })
        if ($cancel.Count -eq 1) {
            Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('invoke',$cancel[0].selector) | Out-Null
            Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('wait-for',$fixture.subject,'--timeout','10000') | Out-Null
            Open-MailAuditContext $RunDirectory DELETE-001 $Window $fixture.subject
            Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('invoke','MailContextOperation_SoftDelete') | Out-Null
            $elements=Get-MailAuditElements $RunDirectory DELETE-001 $Window
            $yes=Find-MailAuditExact $elements @{type='Button';automationId='PrimaryButton';name='Yes'}
            Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('invoke',$yes.selector) | Out-Null
            $elements=Get-MailAuditElements $RunDirectory DELETE-001 $Window
        }
        $undo=@($elements | Where-Object { $_.automationId -eq 'MailListPageUndoMailActionButton' -and $_.isEnabled -and -not $_.isOffscreen })
        if ($undo.Count -eq 1) {
            $elapsed=$timer.Elapsed.TotalMilliseconds
            Invoke-MailAuditUi $RunDirectory UNDO-001 $Window @('invoke',$undo[0].selector) | Out-Null
            Invoke-MailAuditUi $RunDirectory UNDO-001 $Window @('wait-for',$fixture.subject,'--timeout','10000') | Out-Null
            Invoke-MailAuditUi $RunDirectory UNDO-001 $Window @('invoke','ShellSynchronizationButton') | Out-Null
            Invoke-MailAuditUi $RunDirectory UNDO-001 $Window @('wait-for',"Syncing $($account.name)",'--gone','--timeout','120000') | Out-Null
            $sent=Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory UNDO-001 $Window) Sent
            Select-MailAuditFolder $RunDirectory UNDO-001 $Window $sent
            Select-MailAuditFolder $RunDirectory UNDO-001 $Window $folder
            Invoke-MailAuditUi $RunDirectory UNDO-001 $Window @('wait-for',$fixture.subject,'--timeout','10000') | Out-Null
            $path="evidence/$($account.name)-undo-restored.png"
            Invoke-MailAuditUi $RunDirectory UNDO-001 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
            Add-MailAuditResult $RunDirectory UNDO-001 $account.name PASS 'Undo restores dedicated fixture' "Undo invoked after $([int]$elapsed) ms. Exact fixture restored after sync and folder roundtrip." @('commands.jsonl',$path)
            Open-MailAuditContext $RunDirectory DELETE-001 $Window $fixture.subject
            @{fixture=$fixture.id;account=$account.name;action='SoftDelete after verified Undo';state='intended'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
            Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('invoke','MailContextOperation_SoftDelete') | Out-Null
        } else {
            Add-MailAuditResult $RunDirectory UNDO-001 $account.name UNSUPPORTED 'Undo offered for queued deletion' "No enabled visible Undo action exposed $([int]$timer.Elapsed.TotalMilliseconds) ms after deletion. Current setting/timing does not provide an exercised Undo path." @('commands.jsonl')
        }
        Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('wait-for',$fixture.subject,'--gone','--timeout','10000') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('invoke','ShellSynchronizationButton') | Out-Null
        Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('wait-for',"Syncing $($account.name)",'--gone','--timeout','120000') | Out-Null
        $trash=Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory DELETE-001 $Window) Trash
        Select-MailAuditFolder $RunDirectory DELETE-001 $Window $trash
        Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('wait-for',$fixture.subject,'--timeout','120000') | Out-Null
        $path="evidence/$($account.name)-single-deleted.png"
        Invoke-MailAuditUi $RunDirectory DELETE-001 $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
        Set-MailAuditFixture $RunDirectory $fixture.id deleted "$($account.name)/$trash" @('commands.jsonl',$path)
        Add-MailAuditResult $RunDirectory DELETE-001 $account.name PASS 'Single fixture leaves source and appears in Trash' "Exact fixture removed from $folder and verified in $trash after sync. HWND $Window; Light theme." @('commands.jsonl',$path)
        Write-Output "Single deletion checked: $($account.name)"
    } catch { Add-MailAuditResult $RunDirectory DELETE-001 $account.name FAIL 'Delete single audit fixture' "$_" @('commands.jsonl'); throw 'Reconcile deletion state before continuing.' }
}
