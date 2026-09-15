#requires -Version 7.0
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$Window,[Parameter(Mandatory)][string[]]$Accounts)
$ErrorActionPreference='Stop'
Import-Module "$PSScriptRoot/MailAudit.Common.psm1" -Force
$config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
$records=@(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
foreach ($name in $Accounts) {
    $account=Find-MailAuditExact @($config.accounts) @{name=$name}
    $fixture=Find-MailAuditExact @($records | Where-Object { $_.purpose -eq 'draft-attachment' -and $_.state -eq 'received' }) @{recipient=$account.address}
    Select-MailAuditAccount $RunDirectory ATTACH-002 $Window $name
    Select-MailAuditFolder $RunDirectory ATTACH-002 $Window (($fixture.folder -split '/',2)[1])
    $elements=Get-MailAuditElements $RunDirectory ATTACH-002 $Window
    try { $row=Get-MailAuditSingleRow $elements $fixture.subject } catch {
        $thread=Get-MailAuditThread $elements ('Re: FW: '+$fixture.subject)
        if ($thread.expandState -eq 'collapsed') { Invoke-MailAuditUi $RunDirectory ATTACH-002 $Window @('invoke',$thread.selector) | Out-Null }
        $row=Get-MailAuditSingleRow (Get-MailAuditElements $RunDirectory ATTACH-002 $Window) $fixture.subject
    }
    Invoke-MailAuditUi $RunDirectory ATTACH-002 $Window @('invoke',$row.selector) | Out-Null
    $sender=Find-MailAuditExact @($config.accounts) @{name=$fixture.account}
    Assert-MailAuditReader $RunDirectory ATTACH-002 $Window $fixture $sender.address
    Invoke-MailAuditUi $RunDirectory ATTACH-002 $Window @('wait-for','AttachmentsListView','--timeout','10000') | Out-Null
    $elements=Get-MailAuditElements $RunDirectory ATTACH-002 $Window
    $list=Find-MailAuditExact $elements @{type='List';automationId='AttachmentsListView'}
    if (@($list.children).Count -ne 1) { throw 'Expected one attachment.' }
    Invoke-MailAuditUi $RunDirectory ATTACH-002 $Window @('invoke',$list.children[0].selector) | Out-Null
    $viewer=@(); $dialogHandled=$false
    $downloadTimer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $windows=Invoke-MailAuditCommand $RunDirectory ATTACH-002 @('ui','list-windows','--json') | ConvertFrom-Json
        $dialogs=@($windows | Where-Object { $_.title -eq 'File Download' -and [string]$_.ownerHwnd -eq $Window })
        if ($dialogs.Count -eq 1 -and -not $dialogHandled) {
            $dialogWindow=[string]$dialogs[0].hwnd
            $elements=Get-MailAuditElements $RunDirectory ATTACH-002 $dialogWindow
            $open=Find-MailAuditExact $elements @{type='Button';name='Open';automationId='4426'}
            Invoke-MailAuditUi $RunDirectory ATTACH-002 $dialogWindow @('invoke',$open.selector) | Out-Null
            $dialogHandled=$true
        }
        $viewer=@($windows | Where-Object { $_.processName -eq 'Photos' -and $_.title -eq 'Vesika.png' })
        if ($viewer.Count) { break }
        Start-Sleep -Milliseconds 250
    } while ($downloadTimer.Elapsed.TotalSeconds -lt 120)
    if ($viewer.Count -ne 1) { throw 'Expected one Photos window for Vesika.png; reconcile before continuing.' }
    $viewerWindow=[string]$viewer[0].hwnd
    $path="evidence/$name-attachment-preview.png"
    Invoke-MailAuditUi $RunDirectory ATTACH-002 $viewerWindow @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
    $elements=Get-MailAuditElements $RunDirectory ATTACH-002 $viewerWindow
    $files=@($elements | Where-Object { $_.type -eq 'Text' -and $_.name -like 'C:\*\Vesika.png' })
    if (-not $files.Count) {
        Invoke-MailAuditUi $RunDirectory ATTACH-002 $viewerWindow @('invoke','InfoPaneButton') | Out-Null
        $elements=Get-MailAuditElements $RunDirectory ATTACH-002 $viewerWindow
        $files=@($elements | Where-Object { $_.type -eq 'Text' -and $_.name -like 'C:\*\Vesika.png' })
    }
    if ($files.Count -ne 1) { throw 'Downloaded path not uniquely exposed by Photos File info.' }
    $actual=(Get-FileHash -LiteralPath $files[0].name).Hash
    $expected=(Get-FileHash -LiteralPath $config.attachmentPath).Hash
    $hashPath="evidence/$name-attachment-hash.json"
    @{account=$name;download=$files[0].name;actual=$actual;expected=$expected;match=($actual -eq $expected)} | ConvertTo-Json | Set-Content (Join-Path $RunDirectory $hashPath)
    $close=@($elements | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Close' -and -not $_.isOffscreen })[0]
    Invoke-MailAuditUi $RunDirectory ATTACH-002 $viewerWindow @('invoke',$close.selector) | Out-Null
    if ($actual -ne $expected) { throw 'Downloaded bytes differ from source.' }
    Add-MailAuditResult $RunDirectory ATTACH-002 $name PASS 'Received attachment opens and downloaded bytes match' "Vesika.png opened in Photos HWND $viewerWindow. UI-exposed cache path hashed equal to source. Screenshot needs visual review. Mail HWND $Window Light; Photos Dark." @('commands.jsonl',$path,$hashPath)
    Write-Output "Attachment opened and hashed: $name"
}
