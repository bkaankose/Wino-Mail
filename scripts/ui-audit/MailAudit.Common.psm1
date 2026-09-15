#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-MailAuditActive {
    param([string]$RunDirectory)
    if (Test-Path "$RunDirectory/PAUSE") { throw 'Audit paused. Reconcile before resuming.' }
    if (-not (Test-Path "$RunDirectory/run.json")) { throw 'Mail audit run not found.' }
}

function Invoke-MailAuditCommand {
    param([string]$RunDirectory, [string]$ScenarioId, [string[]]$Arguments, [int]$TimeoutSeconds=120)
    Assert-MailAuditActive $RunDirectory
    try {
        & "$PSScriptRoot/Invoke-WinoRecorded.ps1" -RunDirectory $RunDirectory -ScenarioId $ScenarioId -ArgumentList $Arguments -TimeoutSeconds $TimeoutSeconds
    } catch {
        # UIA can invalidate a read while a folder refresh replaces the tree. Never retry input or mutations here.
        if ($Arguments.Count -gt 1 -and $Arguments[0] -eq 'ui' -and $Arguments[1] -in @('inspect','search','wait-for','get-value','get-property') -and $_.Exception.Message -like '*stale_element*') {
            Start-Sleep -Milliseconds 250
            & "$PSScriptRoot/Invoke-WinoRecorded.ps1" -RunDirectory $RunDirectory -ScenarioId $ScenarioId -ArgumentList $Arguments -TimeoutSeconds $TimeoutSeconds
        } else { throw }
    }
}

function Find-MailAuditExact {
    param([object[]]$Items, [hashtable]$Properties)
    $matches = @($Items | Where-Object {
        $item = $_
        $valid = $true
        foreach ($key in $Properties.Keys) {
            if (-not $item.PSObject.Properties[$key] -or [string]$item.$key -cne [string]$Properties[$key]) { $valid=$false }
        }
        $valid
    })
    if ($matches.Count -ne 1) { throw "Expected one exact match; found $($matches.Count)." }
    $matches[0]
}

function Expand-MailAuditElements {
    param([object[]]$Elements)
    foreach ($element in $Elements) {
        $element
        if ($element.PSObject.Properties['children']) { Expand-MailAuditElements @($element.children) }
    }
}

function Get-MailAuditElements {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[int]$Depth=16)
    $tree = Invoke-MailAuditCommand $RunDirectory $ScenarioId @('ui','inspect','-w',$Window,'--depth',[string]$Depth,'--json') | ConvertFrom-Json
    @(Expand-MailAuditElements @($tree.windows | ForEach-Object { $_.elements }))
}

function Invoke-MailAuditUi {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string[]]$Arguments)
    Invoke-MailAuditCommand $RunDirectory $ScenarioId (@('ui') + $Arguments + @('-w',$Window,'--json'))
}

function Select-MailAuditAccount {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string]$Account)
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $match=Find-MailAuditExact $elements @{name=$Account;automationId='ShellAccountSelector';type='ListItem'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke',$match.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','ShellSubtitleText','--value',"$Account - ",'--contains','--timeout','10000') | Out-Null
}

function Select-MailAuditFolder {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string]$Folder)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
        $matches=@($elements | Where-Object { $_.PSObject.Properties['name'] -and $_.name -ceq $Folder -and $_.PSObject.Properties['automationId'] -and $_.automationId -eq 'XBindFolderNameModeOneWay' -and $_.type -eq 'ListItem' })
        if ($matches.Count -gt 1) { throw 'Ambiguous exact folder selector.' }
        if ($matches.Count -eq 1) { $match=$matches[0]; break }
        if ($timer.Elapsed.TotalSeconds -ge 10) { throw "Folder $Folder did not appear within 10 seconds." }
        Start-Sleep -Milliseconds 250
    } while ($true)
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke',$match.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','ShellSubtitleText','--value'," - $Folder",'--contains','--timeout','10000') | Out-Null
}

function Resolve-MailAuditFolder {
    param([object[]]$Elements,[ValidateSet('Inbox','Sent','Drafts','Trash','Junk','Archive')][string]$Role)
    $labels=switch ($Role) {
        Sent { @('Sent','Sent Items','Sent Messages') }
        Drafts { @('Draft','Drafts') }
        Trash { @('Trash','Bin','Deleted Items','Deleted Messages') }
        Junk { @('Junk','Junk Email','Spam') }
        default { @($Role) }
    }
    $matches=@($Elements | Where-Object { $_.PSObject.Properties['automationId'] -and $_.automationId -eq 'XBindFolderNameModeOneWay' -and $_.type -eq 'ListItem' -and $_.name -in $labels })
    if ($matches.Count -ne 1) { throw "Expected one $Role folder; found $($matches.Count). Inspect More or document the unavailable destination." }
    $matches[0].name
}

function Wait-MailAuditFolderRole {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string]$Role)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        try { return Resolve-MailAuditFolder (Get-MailAuditElements $RunDirectory $ScenarioId $Window) $Role }
        catch {
            if ($_.Exception.Message -notlike '*found 0.*') { throw }
            if ($timer.Elapsed.TotalSeconds -ge 10) { throw }
            Start-Sleep -Milliseconds 250
        }
    } while ($true)
}

function Open-MailAuditContext {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string]$Subject)
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    $baseSubject=$Subject -replace '^(?i:(?:re|fw|fwd):\s*)+',''
    if (-not $baseSubject.StartsWith($config.prefix+'-', [StringComparison]::Ordinal)) { throw 'Only this run audit subjects are allowed.' }
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $subjects=@($elements | Where-Object { $_.PSObject.Properties['automationId'] -and $_.automationId -in @('ReadSubjectTextBlock','UnreadSubjectTextBlock') })
    $threads=@($elements | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['expandState'] -and @(Expand-MailAuditElements $_.children | Where-Object { $_.PSObject.Properties['name'] -and $_.name -eq $Subject }).Count -gt 0 })
    if ($threads.Count -eq 1) { $subjects=@(Expand-MailAuditElements $threads[0].children | Where-Object { $_.PSObject.Properties['automationId'] -and $_.automationId -in @('ReadSubjectTextBlock','UnreadSubjectTextBlock') }) }
    $match=Find-MailAuditExact $subjects @{name=$Subject;type='Text'}
    $search=Find-MailAuditExact $elements @{type='Edit';name='Search';automationId='TextBox'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('focus',$search.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click',$match.selector,'--right') | Out-Null
}

function Test-MailAuditContextTransition {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string]$Account,[string]$Subject,[string]$Operation,[string]$ExpectedOperation)
    @{scenario=$ScenarioId;account=$Account;subject=$Subject;operation=$Operation;next='Apply once, then inspect the opposite action.'} | ConvertTo-Json | Set-Content "$RunDirectory/scenario-checkpoint.json"
    Open-MailAuditContext $RunDirectory $ScenarioId $Window $Subject
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke',$Operation) | Out-Null
    Open-MailAuditContext $RunDirectory $ScenarioId $Window $Subject
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for',$ExpectedOperation,'--timeout','10000') | Out-Null
    $path="evidence/$Account-$ScenarioId.png"
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('screenshot','-o',(Join-Path $RunDirectory $path)) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys','escape','--via','send-input') | Out-Null
    Add-MailAuditResult $RunDirectory $ScenarioId $Account PASS $ExpectedOperation "Exact fixture context changed from $Operation to $ExpectedOperation. PID/HWND: $Window; Light theme. Persistence remains a separate check." @('commands.jsonl',$path)
}

function New-MailAuditCompose {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[object]$Fixture,[switch]$UseShortcut)
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    $account=Find-MailAuditExact @($config.accounts) @{name=$Fixture.account}
    if ($Fixture.recipient -notin @($config.accounts | ForEach-Object { $_.address })) { throw 'Recipient is not a configured account.' }
    Select-MailAuditAccount $RunDirectory $ScenarioId $Window $Fixture.account
    if ($UseShortcut) { Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys','ctrl+n','--via','send-input') | Out-Null }
    else { Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke','XBindDomainTranslatorMenuNewMailModeOneTime') | Out-Null }
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','SubjectTextBox','--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','AccountsComboBox','--value',$account.address,'--timeout','10000') | Out-Null
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $toBox=Find-MailAuditExact $elements @{automationId='ToBox';type='List'}
    $input=Find-MailAuditExact @(Expand-MailAuditElements $toBox.children) @{type='Edit';automationId='TextBox'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('focus',$input.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys',$Fixture.recipient,'--target',$input.selector,'--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys','enter','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('focus','SubjectTextBox') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('set-value','SubjectTextBox',$Fixture.subject) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click','wino-editor') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys','ctrl+home','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys',$Fixture.body,'--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','wino-editor','--value',$Fixture.body,'--contains','--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','SubjectTextBox','--value',$Fixture.subject,'--timeout','10000') | Out-Null
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $toBox=Find-MailAuditExact $elements @{automationId='ToBox';type='List'}
    $recipients=@($toBox.children | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['name'] -and $_.name })
    $null=Find-MailAuditExact $recipients @{name=$Fixture.recipient}
    if ($recipients.Count -ne 1) { throw 'Unexpected recipient count. Do not send.' }
    Set-MailAuditFixture $RunDirectory $Fixture.id draft Drafts @('commands.jsonl')
}

function Complete-MailAuditReply {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[object]$Fixture)
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    $account=Find-MailAuditExact @($config.accounts) @{name=$Fixture.account}
    if ($Fixture.recipient -notin @($config.accounts | ForEach-Object { $_.address })) { throw 'Unknown reply recipient.' }
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','AccountsComboBox','--value',$account.address,'--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','SubjectTextBox','--value',$Fixture.subject,'--timeout','10000') | Out-Null
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $toBox=Find-MailAuditExact $elements @{automationId='ToBox';type='List'}
    $recipients=@($toBox.children | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['name'] -and $_.name })
    $null=Find-MailAuditExact $recipients @{name=$Fixture.recipient}
    if ($recipients.Count -ne 1) { throw 'Unexpected reply recipients.' }
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('focus','SubjectTextBox') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click','wino-editor') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys','ctrl+home','--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys',$Fixture.body,'--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','wino-editor','--value',$Fixture.body,'--contains','--timeout','10000') | Out-Null
    Set-MailAuditFixture $RunDirectory $Fixture.id send-intended Drafts @()
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke','ComposePageAppBarButton4') | Out-Null
}

function Add-MailAuditToRecipient {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string]$Address)
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    if ($Address -notin @($config.accounts | ForEach-Object address)) { throw 'Recipient is not configured.' }
    $to=Find-MailAuditExact (Get-MailAuditElements $RunDirectory $ScenarioId $Window) @{type='List';automationId='ToBox'}
    if (@($to.children | Where-Object { $_.PSObject.Properties['name'] -and $_.name -eq $Address }).Count) { throw 'Recipient already present; reconcile instead of adding it again.' }
    $input=Find-MailAuditExact @(Expand-MailAuditElements $to.children) @{type='Edit';automationId='TextBox'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('focus',$input.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys',$Address,'--verbatim','--target',$input.selector,'--via','send-input') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('send-keys','enter','--via','send-input') | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $to=Find-MailAuditExact (Get-MailAuditElements $RunDirectory $ScenarioId $Window) @{type='List';automationId='ToBox'}
        $matches=@($to.children | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['name'] -and $_.name -ceq $Address })
        if ($matches.Count -eq 1) { return }
        if ($matches.Count -gt 1) { throw 'Duplicate recipient tokens; do not send.' }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 10)
    throw 'Entered address did not become a recipient token within 10 seconds.'
}

function Assert-MailAuditRecipients {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[string[]]$Expected)
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    if (@($Expected | Where-Object { $_ -notin @($config.accounts | ForEach-Object address) }).Count) { throw 'Expected recipient is not configured.' }
    $boxes=@(Get-MailAuditElements $RunDirectory $ScenarioId $Window | Where-Object { $_.PSObject.Properties['automationId'] -and $_.automationId -in @('ToBox','CcBox','BccBox') -and $_.type -eq 'List' })
    $actual=@($boxes | ForEach-Object { $_.children | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['name'] -and $_.name } } | ForEach-Object name)
    if ($actual.Count -ne $Expected.Count -or @(Compare-Object ($actual | Sort-Object) ($Expected | Sort-Object)).Count) { throw "Unexpected recipient set. Expected $($Expected.Count), found $($actual.Count). Do not send." }
}

function Get-MailAuditThread {
    param([object[]]$Elements,[string]$Subject)
    $matches=@($Elements | Where-Object {
        $_.type -eq 'ListItem' -and $_.PSObject.Properties['expandState'] -and
        @(Expand-MailAuditElements $_.children | Where-Object { $_.PSObject.Properties['name'] -and $_.name -eq $Subject }).Count -gt 0
    })
    if ($matches.Count -ne 1) { throw "Expected one conversation for the fixture; found $($matches.Count)." }
    $matches[0]
}

function Get-MailAuditSingleRow {
    param([object[]]$Elements,[string]$Subject)
    $list=Find-MailAuditExact $Elements @{type='List';automationId='MailListView'}
    $rows=@(Expand-MailAuditElements $list.children | Where-Object {
        $_.type -eq 'ListItem' -and -not $_.PSObject.Properties['expandState'] -and $_.PSObject.Properties['children'] -and
        @(Expand-MailAuditElements $_.children | Where-Object { $_.PSObject.Properties['name'] -and $_.name -ceq $Subject }).Count -gt 0
    })
    if ($rows.Count -ne 1) { throw "Expected exactly one single row for fixture; found $($rows.Count)." }
    $rows[0]
}

function Assert-MailAuditReader {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[object]$Fixture,[string]$SenderAddress)
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','wino-reader','--value',$Fixture.subject,'--contains','--timeout','10000') | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window -Depth 30
        $readers=@($elements | Where-Object { $_.PSObject.Properties['automationId'] -and $_.automationId -eq 'wino-reader' })
        foreach ($reader in $readers) {
            if ($reader.name -like "*$SenderAddress*" -and $reader.PSObject.Properties['children']) {
                if (@(Expand-MailAuditElements $reader.children | Where-Object { $_.PSObject.Properties['name'] -and $_.name -like "*$($Fixture.body)*" }).Count) { return }
            }
        }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 10)
    throw 'Reader did not expose the expected sender and body token within 10 seconds.'
}

function Set-MailAuditThreading {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[bool]$Enabled)
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $search=Find-MailAuditExact $elements @{type='Edit';name='Search';automationId='TextBox'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('focus',$search.selector) | Out-Null
    $settings=Find-MailAuditExact $elements @{type='ListItem';name='Settings'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click',$settings.selector) | Out-Null
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $pages=@($elements | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['name'] -and $_.name -eq 'Message List' })
    if (-not $pages.Count) {
        $groups=@($elements | Where-Object { $_.type -eq 'ListItem' -and $_.PSObject.Properties['automationId'] -and $_.automationId -like 'SettingsShellGroup*' })
        $mail=Find-MailAuditExact $groups @{name='Mail'}
        Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click',$mail.selector) | Out-Null
        $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    }
    $page=Find-MailAuditExact $elements @{type='ListItem';name='Message List'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click',$page.selector) | Out-Null
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    if (-not @($elements | Where-Object { $_.PSObject.Properties['automationId'] -and $_.automationId -eq 'MessageListPageToggleSwitch3' }).Count) {
        $expander=Find-MailAuditExact $elements @{type='Button';name='Conversation Threading'}
        Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke',$expander.selector) | Out-Null
    }
    $current=Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('get-value','MessageListPageToggleSwitch3') | ConvertFrom-Json
    $expected=if ($Enabled) { 'On' } else { 'Off' }
    if ($current.text -ne $expected) { Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke','MessageListPageToggleSwitch3') | Out-Null }
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for','MessageListPageToggleSwitch3','--value',$expected,'--timeout','10000') | Out-Null
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
    $modes=@($elements | Where-Object { $_.type -eq 'ListItem' -and -not $_.PSObject.Properties['automationId'] })
    $mail=Find-MailAuditExact $modes @{name='Mail'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('click',$mail.selector) | Out-Null
}

function Add-MailAuditAttachment {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Window,[switch]$PickerAlreadyOpen)
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    if (-not (Test-Path -LiteralPath $config.attachmentPath)) { throw 'Attachment file is missing.' }
    if (-not $PickerAlreadyOpen) {
        $elements=Get-MailAuditElements $RunDirectory $ScenarioId $Window
        $insert=Find-MailAuditExact $elements @{type='TabItem';name='Insert'}
        Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke',$insert.selector) | Out-Null
        Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('invoke','FilesButton') | Out-Null
    }
    $picker=@()
    for ($attempt=0;$attempt -lt 20;$attempt++) {
        $windows=Invoke-MailAuditCommand $RunDirectory $ScenarioId @('ui','list-windows','--json') | ConvertFrom-Json
        $picker=@($windows | Where-Object { [string]$_.ownerHwnd -eq $Window -and $_.title -eq 'Open' })
        if ($picker.Count) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($picker.Count -ne 1) { throw 'Expected one owned Open picker.' }
    $pickerWindow=[string]$picker[0].hwnd
    $elements=Get-MailAuditElements $RunDirectory $ScenarioId $pickerWindow
    $filename=Find-MailAuditExact $elements @{type='Edit';name='File name:'}
    $open=Find-MailAuditExact $elements @{type='SplitButton';name='Open';automationId='1'}
    Invoke-MailAuditUi $RunDirectory $ScenarioId $pickerWindow @('focus',$filename.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $pickerWindow @('set-value',$filename.selector,$config.attachmentPath) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $pickerWindow @('wait-for',$filename.selector,'--value',$config.attachmentPath,'--timeout','10000') | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $pickerWindow @('invoke',$open.selector) | Out-Null
    Invoke-MailAuditUi $RunDirectory $ScenarioId $Window @('wait-for',([IO.Path]::GetFileName($config.attachmentPath)),'--timeout','10000') | Out-Null
}

function Set-MailAuditAccounts {
    param([string]$RunDirectory,[object[]]$Accounts)
    Assert-MailAuditActive $RunDirectory
    if (-not $Accounts.Count) { throw 'No configured accounts were discovered.' }
    if (@($Accounts.name | Select-Object -Unique).Count -ne $Accounts.Count) { throw 'Ambiguous account names require explicit UI identity resolution.' }
    foreach ($account in $Accounts) {
        if (-not $account.name -or -not $account.address -or -not $account.provider) { throw 'Account name, sending address and provider are required.' }
        $null=[System.Net.Mail.MailAddress]::new($account.address)
    }
    $addresses=@($Accounts | ForEach-Object { $_.address } | Sort-Object -Unique)
    $configured=foreach ($account in $Accounts) {
        $index=[Array]::IndexOf($addresses,$account.address)
        $recipient=if ($addresses.Count -gt 1) { $addresses[($index+1)%$addresses.Count] } else { $null }
        [pscustomobject]@{name=$account.name;address=$account.address;provider=$account.provider;recipient=$recipient}
    }
    $config=Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    $config.accounts=@($configured)
    $config | ConvertTo-Json -Depth 10 | Set-Content "$RunDirectory/run.json"
    $configured
}

function Wait-MailAuditCondition {
    param([scriptblock]$Probe, [ValidateRange(1,120)][int]$TimeoutSeconds=10, [int]$IntervalMilliseconds=500)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Probe) { return }
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    } while ($true)
    throw "Assertion timed out after $TimeoutSeconds seconds. Do not repeat a send."
}

function Add-MailAuditFixture {
    param([string]$RunDirectory, [string]$Account, [string]$ScenarioId, [string]$Recipient, [string]$Purpose)
    Assert-MailAuditActive $RunDirectory
    $config = Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    $id = [guid]::NewGuid().ToString('N')
    $record = [pscustomobject]@{id=$id;account=$Account;recipient=$Recipient;scenario=$ScenarioId;purpose=$Purpose;subject="$($config.prefix)-$Account-$ScenarioId-$($id.Substring(0,6))";body="Mail UI audit. Verification token: $id";state='intended';folder='unverified';evidence=@()}
    $records = @(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json) + @($record)
    ConvertTo-Json -InputObject $records -Depth 10 | Set-Content "$RunDirectory/records.json"
    $record
}

function Set-MailAuditFixture {
    param([string]$RunDirectory,[string]$Id,[ValidateSet('draft','send-intended','sent','received','moved','deleted','uncertain')][string]$State,[string]$Folder,[string[]]$Evidence)
    Assert-MailAuditActive $RunDirectory
    $records = @(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    $record = Find-MailAuditExact $records @{id=$Id}
    if ($State -eq 'draft' -and $record.state -notin @('intended','draft')) { throw 'A send attempt cannot be reset to a sendable draft. Record reconciliation separately.' }
    if ($State -eq 'send-intended' -and $record.state -notin @('intended','draft')) { throw 'Send already attempted. Reconcile through UI; never resend automatically.' }
    if ($State -ne 'send-intended' -and -not $Evidence) { throw 'State changes require evidence.' }
    $record.state=$State; $record.folder=$Folder; $record.evidence=@($Evidence)
    ConvertTo-Json -InputObject $records -Depth 10 | Set-Content "$RunDirectory/records.json"
}

function Add-MailAuditResult {
    param([string]$RunDirectory,[string]$ScenarioId,[string]$Account='GLOBAL',
        [ValidateSet('PASS','FAIL','BLOCKED','UNSUPPORTED','NOT-RUN')][string]$Status,
        [string]$Expected,[string]$Actual,[string[]]$Evidence,[string]$Stage='UI')
    if ($Status -eq 'PASS' -and (-not $Evidence -or -not $Actual)) { throw 'PASS requires observed assertions and evidence.' }
    $manifest = @(Get-Content "$RunDirectory/scenarios.json" -Raw | ConvertFrom-Json)
    $null = Find-MailAuditExact $manifest @{id=$ScenarioId}
    @{scenario=$ScenarioId;account=$Account;status=$Status;expected=$Expected;actual=$Actual;evidence=@($Evidence);stage=$Stage;time=[DateTimeOffset]::Now.ToString('o')} |
        ConvertTo-Json -Compress -Depth 10 | Add-Content "$RunDirectory/results.jsonl"
}

function Test-MailAuditDependencies {
    param([object[]]$Results,[string[]]$Dependencies,[string]$Account,[object[]]$Scenarios=@())
    foreach ($dependency in $Dependencies) {
        $observations=@($Results | Where-Object { $_.scenario -eq $dependency -and $_.account -in @($Account,'GLOBAL') })
        if (-not $observations.Count) { return $false }
        $latest=@($observations | Group-Object { if ($_.PSObject.Properties['stage']) { $_.stage } else { 'UI' } } | ForEach-Object { $_.Group | Select-Object -Last 1 })
        if (@($latest | Where-Object status -ne PASS).Count) { return $false }
        $definition=@($Scenarios | Where-Object id -eq $dependency)
        if ($definition.Count -eq 1 -and $definition[0].PSObject.Properties['requiredStages']) {
            $stages=@($latest | ForEach-Object { if ($_.PSObject.Properties['stage']) { $_.stage } else { 'UI' } })
            if (@($definition[0].requiredStages | Where-Object { $_ -notin $stages }).Count) { return $false }
        }
    }
    return $true
}

function Export-MailAuditReport {
    param([string]$RunDirectory)
    $config = Get-Content "$RunDirectory/run.json" -Raw | ConvertFrom-Json
    $scenarios = @(Get-Content "$RunDirectory/scenarios.json" -Raw | ConvertFrom-Json)
    $results = @(Get-Content "$RunDirectory/results.jsonl" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $coverage = @()
    foreach ($scenario in $scenarios) {
        $accounts = if ($scenario.scope -eq 'global') { @('GLOBAL') } elseif (@($config.accounts).Count) { @($config.accounts | ForEach-Object { $_.name }) } else { @('UNDISCOVERED') }
        foreach ($account in $accounts) {
            $observations=@($results | Where-Object { $_.scenario -eq $scenario.id -and $_.account -eq $account } | Group-Object stage | ForEach-Object { $_.Group | Select-Object -Last 1 })
            $required=if ($scenario.PSObject.Properties['requiredStages']) { @($scenario.requiredStages) } else { @('UI') }
            $missing=@($required | Where-Object { $_ -notin @($observations | ForEach-Object stage) })
            $status='PASS'
            if (-not $observations.Count) { $status='NOT-RUN' }
            elseif (@($observations | Where-Object status -eq FAIL).Count) { $status='FAIL' }
            elseif (@($observations | Where-Object status -eq BLOCKED).Count) { $status='BLOCKED' }
            elseif (@($observations | Where-Object status -eq UNSUPPORTED).Count) { $status='UNSUPPORTED' }
            elseif ($missing.Count -or @($observations | Where-Object status -eq NOT-RUN).Count) { $status='NOT-RUN' }
            $actual=if ($observations.Count) { ($observations | ForEach-Object { "$($_.stage): $($_.actual)" }) -join ' / ' } else { 'No execution evidence.' }
            if ($missing.Count) { $actual += ' Missing stages: ' + ($missing -join ', ') }
            $result=[pscustomobject]@{scenario=$scenario.id;account=$account;status=$status;actual=$actual;evidence=@($observations | ForEach-Object evidence);observations=$observations;missingStages=$missing}
            $coverage += $result
        }
    }
    ConvertTo-Json -InputObject $coverage -Depth 10 | Set-Content "$RunDirectory/coverage.json"
    $counts=($coverage | Group-Object status | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    $lines = @('# Mail audit results','','This report records observed outcomes. It does not certify unattended execution or independent server persistence.',"","Run state: $($config.state). Coverage: $counts.",'','[Account matrix](coverage-matrix.md) · [Findings](findings.md) · [Fixture ledger](cleanup.md)','','| Account | Scenario | Result | Observation |','|---|---|---|---|')
    foreach ($item in $coverage) { $lines += "| $($item.account) | $($item.scenario) | $($item.status) | $($item.actual -replace '\|','/' -replace '[\r\n]+',' ') |" }
    $lines += @('','','See results.jsonl for assertion stages and evidence paths. See records.json for retained messages and uncertain actions.')
    $lines | Set-Content "$RunDirectory/report.md"
    $names=@('GLOBAL')+@($config.accounts | ForEach-Object name)
    $matrixHeader='| Scenario | '+($names -join ' | ')+' |'
    $matrixSeparator='|---|'+('---|'*$names.Count)
    $matrix=@('# Mail account coverage matrix','','A dash means that the scenario does not have that account scope. NOT-RUN never means PASS.','',$matrixHeader,$matrixSeparator)
    foreach ($scenario in $scenarios) {
        $cells=foreach ($name in $names) {
            $match=@($coverage | Where-Object { $_.scenario -eq $scenario.id -and $_.account -eq $name })
            if ($match.Count) { $match[0].status } else { '—' }
        }
        $matrix += '| '+$scenario.id+' | '+($cells -join ' | ')+' |'
    }
    $matrix | Set-Content "$RunDirectory/coverage-matrix.md"
    $records = @(Get-Content "$RunDirectory/records.json" -Raw | ConvertFrom-Json)
    $ledger=@('# Retained audit fixtures','','Messages stay in their last verified folders. No permanent deletion is permitted.',
        'A discarded draft uses ledger state `deleted`. Sent and recipient copies are separate observations.',
        'See `records.json` for full identities and `fixture-observations.jsonl` for verified delivery copies.',
        '', '| Creator | Purpose | Subject | State | Last recorded folder |', '|---|---|---|---|---|')
    foreach ($record in $records) {
        $cells=@($record.account,$record.purpose,$record.subject,$record.state,$record.folder) | ForEach-Object { ([string]$_).Replace('|','\|').Replace("`n",' ') }
        $ledger+='| '+($cells -join ' | ')+' |'
    }
    $ledger | Set-Content "$RunDirectory/cleanup.md"
}

Export-ModuleMember -Function *-MailAudit*
