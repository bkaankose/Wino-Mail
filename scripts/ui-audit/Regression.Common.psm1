# Shared helpers for finite, recorded UI regressions. Imported only by this runner and its tests.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Initialize-WinoRegression {
    param([Parameter(Mandatory)][hashtable]$Context)
    $script:Context = $Context
}

function Invoke-RegressionCommand {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if (Test-Path (Join-Path $script:Context.RunDirectory 'PAUSE')) { throw 'Audit paused. Reconcile before resuming.' }
    & $script:Context.Invoke $Arguments $script:Context.Scenario
}

function Update-RegressionWindow {
    $deadline = [DateTimeOffset]::Now.AddSeconds(10)
    do {
        $windows = @(Invoke-RegressionCommand @('ui','list-windows','-a','Wino.Mail.WinUI','--json') | ConvertFrom-Json)
        $main = @($windows | Where-Object { $_.label -eq 'window' -and $_.ownerHwnd -eq 0 })
        if ($main.Count -gt 0) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::Now -lt $deadline)
    if ($main.Count -ne 1) { throw "Expected one Wino main window; found $($main.Count)." }
    $script:Context.Window = [string]$main[0].hwnd
    $script:Context.ProcessId = [int]$main[0].processId
    return $main[0]
}

function Invoke-RegressionUi {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if (-not $script:Context.Window) { throw 'Resolve the current window before issuing UI commands.' }
    Invoke-RegressionCommand (@('ui') + $Arguments + @('-w',$script:Context.Window,'--json'))
}

function Wait-RegressionElement {
    param([string]$Selector, [AllowEmptyString()][string]$Value, [switch]$Gone)
    $arguments = @('wait-for',$Selector,'--timeout','10000')
    if ($PSBoundParameters.ContainsKey('Value')) { $arguments += @('--value',$Value) }
    if ($Gone) { $arguments += '--gone' }
    Invoke-RegressionUi $arguments | Out-Null
}

function Set-RegressionValue {
    param([string]$Selector, [string]$Value)
    Invoke-RegressionUi @('focus',$Selector) | Out-Null
    Invoke-RegressionUi @('set-value',$Selector,$Value) | Out-Null
    Wait-RegressionElement $Selector -Value $Value
}

function Expand-RegressionElements {
    param([object[]]$Elements)
    foreach ($element in $Elements) {
        $element
        if ($element.PSObject.Properties.Name -contains 'children' -and $element.children) {
            Expand-RegressionElements @($element.children)
        }
    }
}

function Find-RegressionElement {
    param([string]$Name, [string]$AutomationId, [string]$Type, [string]$Scope)
    $arguments = @('inspect')
    if ($Scope) { $arguments += $Scope }
    $arguments += @('--interactive','--depth','14')
    $tree = Invoke-RegressionUi $arguments | ConvertFrom-Json
    $elements = @(Expand-RegressionElements @($tree.windows | ForEach-Object { $_.elements }))
    $matching = @($elements | Where-Object {
        (-not $Name -or $_.name -ceq $Name) -and
        (-not $AutomationId -or $_.automationId -ceq $AutomationId) -and
        (-not $Type -or $_.type -eq $Type) -and
        -not $_.isOffscreen -and $_.selector
    })
    if ($matching.Count -ne 1) { throw "Expected one element: name='$Name', id='$AutomationId', type='$Type'. Found $($matching.Count)." }
    return $matching[0]
}

function Select-RegressionMode {
    param([ValidateSet('Mail','People','To Do','Calendar')][string]$Name)
    $item = Find-RegressionElement -Name $Name -Type ListItem -Scope NavigationView
    Invoke-RegressionUi @('click',$item.selector) | Out-Null
    $expected = switch ($Name) {
        'People' { 'NewContactNavigationItem' }
        'To Do' { 'ToDoPageRoot' }
        'Calendar' { 'NewCalendarEventNavigationItem' }
        'Mail' { 'MailListView' }
    }
    Wait-RegressionElement $expected
}

function Select-RegressionAccount {
    $account = Find-RegressionElement -Name $script:Context.Account -AutomationId ShellAccountSelector -Scope NavigationView
    Invoke-RegressionUi @('invoke',$account.selector) | Out-Null
}

function Set-RegressionRecord {
    param([string]$Kind, [string]$Name, [ValidateSet('intended','confirmed','removed')][string]$State)
    if (-not $Name.StartsWith($script:Context.Prefix + '-', [StringComparison]::Ordinal)) {
        throw 'Only this run prefix can be recorded or mutated.'
    }
    $path = Join-Path $script:Context.RunDirectory 'records.json'
    $records = @(Get-Content $path -Raw | ConvertFrom-Json)
    $record = @($records | Where-Object { $_.kind -eq $Kind -and $_.name -ceq $Name })
    if ($record.Count -gt 1) { throw 'Ambiguous record ledger.' }
    if ($record.Count -eq 1) { $record[0].state = $State }
    else { $records += [pscustomobject]@{ account=$script:Context.Account; kind=$Kind; name=$Name; state=$State } }
    ConvertTo-Json -InputObject @($records) -Depth 5 | Set-Content $path
}

function Confirm-RegressionDialog {
    param([ValidateSet('PrimaryButton','SecondaryButton')][string]$Button='PrimaryButton')
    Wait-RegressionElement $Button
    Invoke-RegressionUi @('focus',$Button) | Out-Null
    Invoke-RegressionUi @('invoke',$Button) | Out-Null
    Wait-RegressionElement $Button -Gone
}

function Restart-RegressionApp {
    # The runner has proven Debug deployment and bound this PID to its executable before scenarios start.
    $process = Get-Process -Id $script:Context.ProcessId -ErrorAction Stop
    if ($process.ProcessName -ne 'Wino.Mail.WinUI' -or $process.Path -ne $script:Context.DebugExecutable) {
        throw 'The process no longer matches this run Debug deployment.'
    }
    Stop-Process -Id $process.Id -ErrorAction Stop
    Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) { throw 'Debug app did not stop.' }
    Invoke-RegressionCommand $script:Context.RelaunchArguments | Out-Null
    $script:Context.Window = ''
    Update-RegressionWindow | Out-Null
}

function Select-RegressionTaskList {
    param([string]$Name)
    Select-RegressionMode 'To Do'
    Select-RegressionAccount
    Wait-RegressionElement $Name
    $list = Find-RegressionElement -Name $Name -AutomationId ToDoListItem
    Invoke-RegressionUi @('invoke',$list.selector) | Out-Null
    Wait-RegressionElement ToDoSelectedSurfaceTitle -Value $Name
}

function Invoke-TasksPersistenceRegression {
    $list = $script:Context.Prefix + '-List'
    $task = $script:Context.Prefix + '-Task'
    $renamed = $task + '-Edited'
    $step = $task + '-Step'
    Select-RegressionMode 'To Do'
    Select-RegressionAccount
    Set-RegressionRecord task-list $list intended
    Invoke-RegressionUi @('click','ToDoNewList') | Out-Null
    Wait-RegressionElement FolderTextBox
    Set-RegressionValue FolderTextBox $list
    Confirm-RegressionDialog
    Select-RegressionTaskList $list
    Set-RegressionRecord task-list $list confirmed
    Set-RegressionValue ToDoNewTaskTextBox $task
    Invoke-RegressionUi @('invoke','ToDoNewTaskButton') | Out-Null
    Wait-RegressionElement ToDoTaskRowTitle -Value $task
    Invoke-RegressionUi @('click','ToDoTaskRowTitle') | Out-Null
    Wait-RegressionElement ToDoTaskTitle -Value $task
    Set-RegressionValue ToDoTaskTitle $renamed
    Set-RegressionValue ToDoTaskNotes 'Synthetic Wino regression notes'
    Invoke-RegressionUi @('focus','ToDoTaskTitle') | Out-Null
    Invoke-RegressionUi @('invoke','ToDoAddStepButton') | Out-Null
    Wait-RegressionElement ToDoStepTitle
    Set-RegressionValue ToDoStepTitle $step
    Invoke-RegressionUi @('focus','ToDoTaskNotes') | Out-Null
    Invoke-RegressionUi @('invoke','ToDoCloseDetailButton') | Out-Null
    Restart-RegressionApp
    Select-RegressionTaskList $list
    # Require exactly one row before opening it. Duplicate titles must fail this scenario.
    $row = Find-RegressionElement -Name $renamed -AutomationId ToDoTaskRowTitle
    Invoke-RegressionUi @('click',$row.selector) | Out-Null
    Wait-RegressionElement ToDoTaskTitle -Value $renamed
    Wait-RegressionElement ToDoTaskNotes -Value 'Synthetic Wino regression notes'
    Wait-RegressionElement ToDoStepTitle -Value $step
    Invoke-RegressionUi @('invoke','ToDoCloseDetailButton') | Out-Null
    Wait-RegressionElement ToDoSelectedSurfaceTitle -Value $list
    Invoke-RegressionUi @('invoke','ToDoListOverflowButton') | Out-Null
    Invoke-RegressionUi @('invoke','ToDoDeleteListButton') | Out-Null
    Confirm-RegressionDialog SecondaryButton
    Wait-RegressionElement ToDoSelectedSurfaceTitle -Value $list
    Invoke-RegressionUi @('invoke','ToDoListOverflowButton') | Out-Null
    Invoke-RegressionUi @('invoke','ToDoDeleteListButton') | Out-Null
    Confirm-RegressionDialog
    Wait-RegressionElement $list -Gone
    Restart-RegressionApp
    Select-RegressionMode 'To Do'
    Select-RegressionAccount
    Wait-RegressionElement $list -Gone
    Set-RegressionRecord task-list $list removed
}

function Select-RegressionContact {
    param([string]$Name)
    Select-RegressionMode People
    Select-RegressionAccount
    $search = Find-RegressionElement -Type Edit -Scope TitleBarSearchBox
    Invoke-RegressionUi @('focus',$search.selector) | Out-Null
    Invoke-RegressionUi @('send-keys','ctrl+a','--target',$search.selector,'--via','send-input') | Out-Null
    Invoke-RegressionUi @('send-keys',"text=$Name",'--target',$search.selector,'--via','send-input') | Out-Null
    Wait-RegressionElement $Name
    Invoke-RegressionUi @('send-keys','enter','--via','send-input') | Out-Null
    Invoke-RegressionUi @('send-keys','escape','--via','send-input') | Out-Null
    Wait-RegressionElement ContactDetailEdit
    Invoke-RegressionUi @('invoke','ContactDetailEdit') | Out-Null
    # A search hit is never sufficient proof for a later edit or deletion.
    Wait-RegressionElement ContactEditorDisplayName -Value $Name
}

function Invoke-ContactsPersistenceRegression {
    $name = $script:Context.Prefix + '-Contact'
    $given = $name + '-Given'
    Select-RegressionMode People
    Select-RegressionAccount
    Set-RegressionRecord contact $name intended
    Invoke-RegressionUi @('click','NewContactNavigationItem') | Out-Null
    Wait-RegressionElement ContactEditorDisplayName
    Set-RegressionValue ContactEditorGivenName $given
    Set-RegressionValue ContactEditorDisplayName $name
    Set-RegressionValue ContactEditorEmail ($name.ToLowerInvariant() + '@example.invalid')
    Invoke-RegressionUi @('invoke','ContactEditorSave') | Out-Null
    Wait-RegressionElement $script:Context.ContactDestination
    $destination = Find-RegressionElement -Name $script:Context.ContactDestination -Type ListItem
    Invoke-RegressionUi @('click',$destination.selector) | Out-Null
    Wait-RegressionElement ContactEditorDisplayName -Gone
    Select-RegressionContact $name
    Wait-RegressionElement ContactEditorGivenName -Value $given
    Set-RegressionRecord contact $name confirmed
    Invoke-RegressionUi @('invoke','ContactEditorAddPhone') | Out-Null
    Wait-RegressionElement ContactEditorPhone
    Set-RegressionValue ContactEditorPhone '+1 202 555 0100'
    Invoke-RegressionUi @('focus','ContactEditorGivenName') | Out-Null
    Invoke-RegressionUi @('invoke','ContactEditorSave') | Out-Null
    Wait-RegressionElement ContactEditorDisplayName -Gone
    Restart-RegressionApp
    Select-RegressionContact $name
    Wait-RegressionElement ContactEditorGivenName -Value $given
    Wait-RegressionElement ContactEditorPhone -Value '+1 202 555 0100'
    Set-RegressionValue ContactEditorDisplayName ($name + '-Discard')
    Invoke-RegressionUi @('invoke','ContactEditorCancel') | Out-Null
    Confirm-RegressionDialog SecondaryButton
    Wait-RegressionElement ContactEditorDisplayName -Value ($name + '-Discard')
    Invoke-RegressionUi @('invoke','ContactEditorCancel') | Out-Null
    Confirm-RegressionDialog
    Select-RegressionContact $name
    Invoke-RegressionUi @('invoke','ContactEditorCancel') | Out-Null
    Wait-RegressionElement ContactDetailDelete
    Invoke-RegressionUi @('invoke','ContactDetailDelete') | Out-Null
    Confirm-RegressionDialog SecondaryButton
    Select-RegressionContact $name
    Invoke-RegressionUi @('invoke','ContactEditorCancel') | Out-Null
    Invoke-RegressionUi @('invoke','ContactDetailDelete') | Out-Null
    Confirm-RegressionDialog
    Wait-RegressionElement $name -Gone
    Set-RegressionRecord contact $name removed
}

function Invoke-ActivationRoutingRegression {
    foreach ($scheme in @('webcal','webcals')) {
        Select-RegressionMode People
        # Empty protocol fixtures exercise routing without fetching an external calendar.
        $result = & $script:Context.Activate ($scheme + ':')
        if (-not $result.accepted) { throw "Windows rejected $scheme activation." }
        $window = Update-RegressionWindow
        if ($window.title -notlike '*Calendar*') { throw 'Activation did not route to Calendar.' }
        Wait-RegressionElement NewCalendarEventNavigationItem
    }
}

Export-ModuleMember -Function Initialize-WinoRegression,Invoke-RegressionCommand,Update-RegressionWindow,Invoke-RegressionUi,Wait-RegressionElement,Set-RegressionValue,Find-RegressionElement,Set-RegressionRecord,Invoke-TasksPersistenceRegression,Invoke-ContactsPersistenceRegression,Invoke-ActivationRoutingRegression
