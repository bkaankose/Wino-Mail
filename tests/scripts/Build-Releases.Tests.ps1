#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/build-releases.ps1')

$script:Passed = 0
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('wino-release-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $script:TestRoot
$script:SavedSettings = @{}
$fixtureSettings = @{
    WINO_BETA_RELEASE_SIGNING_ENDPOINT = 'https://example.codesigning.azure.net'
    WINO_BETA_RELEASE_SIGNING_ACCOUNT_NAME = 'test-account'
    WINO_BETA_RELEASE_SIGNING_CERTIFICATE_PROFILE_NAME = 'test-profile'
    WINO_BETA_RELEASE_PUBLISHER_SUBJECT = $script:SideloadPublisher
    WINO_BETA_RELEASE_APPINSTALLER_URI = ''
    WINO_BETA_RELEASE_PACKAGE_BASE_URI = ''
    WINO_SIDELOAD_RELEASE_APPINSTALLER_URI = ''
    WINO_SIDELOAD_RELEASE_PACKAGE_BASE_URI = ''
    WINO_OPENAI_API_KEY = 'test-translation-secret'
    WINO_AI_OPENAI_TEST_KEY = 'test-ai-secret'
}
foreach ($key in $fixtureSettings.Keys) {
    $script:SavedSettings[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $fixtureSettings[$key])
}

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        Assert-True ($_.Exception.Message -match $Pattern) "Unexpected exception: $($_.Exception.Message)"
        return
    }
    throw "Expected an exception matching $Pattern"
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:Passed++
    Write-Host "PASS $Name"
}
function New-FixturePlan([bool]$Store, [bool]$Beta, [string[]]$Architectures = @('x64'), [bool]$Sideload = $false) {
    $root = Join-Path $script:TestRoot ([guid]::NewGuid().ToString('N'))
    $app = Join-Path $root 'src/Wino.Mail.WinUI'
    $null = New-Item -ItemType Directory -Path $app -Force
    '<Package><Identity Name="Store.App" Publisher="CN=Store" Version="2.53.0.0" /></Package>' |
        Set-Content -LiteralPath (Join-Path $app 'Package.appxmanifest')
    return New-ReleasePlan ([pscustomobject]@{ Store = $Store; Beta = $Beta; Sideload = $Sideload; Architectures = $Architectures }) $root
}

try {
    Test-Case 'Explicit choices retry invalid input and support All' {
        $script:Answers = [Collections.Generic.Queue[string]]::new([string[]]@('', 'maybe', 'yes', 'no', 'invalid', 'yes', 'wrong', 'All'))
        function Read-Host { param($Prompt); return $script:Answers.Dequeue() }
        $result = Read-ReleaseSelection
        Assert-True ($result.Store -and -not $result.Beta -and $result.Sideload) 'Wrong channels.'
        Assert-True (($result.Architectures -join ',') -eq 'x86,x64,ARM64') 'Wrong architectures.'
        Assert-True ($script:Answers.Count -eq 0) 'Invalid answers were not retried.'
    }
    Test-Case 'No channels skips architecture and all preflight work' {
        $script:Answers = [Collections.Generic.Queue[string]]::new([string[]]@('no', 'n', 'no'))
        function Read-Host { param($Prompt); return $script:Answers.Dequeue() }
        function New-ReleasePlan { throw 'Must not inspect or build the project.' }
        Invoke-InteractiveRelease
        Assert-True ($script:Answers.Count -eq 0) 'Unexpected prompts.'
    }
    Test-Case 'Both channels and x64 selection' {
        $script:Answers = [Collections.Generic.Queue[string]]::new([string[]]@('y', 'YES', 'no', '1'))
        function Read-Host { param($Prompt); return $script:Answers.Dequeue() }
        $result = Read-ReleaseSelection
        Assert-True ($result.Store -and $result.Beta -and $result.Architectures.Count -eq 1 -and $result.Architectures[0] -eq 'x64') 'Wrong selection.'
    }
    Test-Case 'Version validation rejects invalid Store versions' {
        Assert-True ((Get-ReleaseVersion '2.53.0.0') -eq '2.53.0.0') 'Version changed.'
        foreach ($invalid in @('2.53.0', '2.53.0.1', '0.53.0.0', '2.65536.0.0', '02.53.0.0', '2.9999999999999.0.0', '../2.0.0.0')) {
            Assert-Throws { Get-ReleaseVersion $invalid } 'version'
        }
    }
    Test-Case 'Selected destination collision preserves old artifacts' {
        $plan = New-FixturePlan $true $false
        $null = New-Item -ItemType Directory -Path $plan.Destinations[0] -Force
        $sentinel = Join-Path $plan.Destinations[0] 'keep.txt'
        'existing release' | Set-Content -LiteralPath $sentinel
        Assert-Throws { New-ReleasePlan $plan.Selection $plan.RepositoryRoot } 'already exists'
        Assert-True ((Get-Content -LiteralPath $sentinel) -eq 'existing release') 'Existing artifact changed.'
        $betaPlan = New-ReleasePlan ([pscustomobject]@{ Store = $false; Beta = $true; Sideload = $false; Architectures = @('x64') }) $plan.RepositoryRoot
        Assert-True ($betaPlan.Destinations.Count -eq 1) 'An unselected destination blocked the run.'
    }
    Test-Case 'Build arguments use Release once and let each bundle architecture select its RID' {
        foreach ($store in @($true, $false)) {
            $plan = New-FixturePlan $store $true @('x86', 'x64', 'ARM64')
            $arguments = Get-ReleaseBuildArguments $plan $script:TestRoot
            $mode = if ($store) { 'StoreUpload' } else { 'SideloadOnly' }
            Assert-True ($arguments -contains "-p:UapAppxPackageBuildMode=$mode") 'Wrong packaging mode.'
            Assert-True ($arguments -contains '-p:AppxBundlePlatforms=x86|x64|ARM64') 'Incomplete architectures.'
            Assert-True (@($arguments | Where-Object { $_ -match 'RuntimeIdentifier=|ReleaseSideload|Restore=true' }).Count -eq 0) 'Build overrides inner runtime or restores twice.'
        }
    }
    Test-Case 'Process arguments are literal and Azure credentials never reach build tools' {
        $oldSecret = $env:WINO_BETA_RELEASE_AZURE_CLIENT_SECRET
        try {
            $env:WINO_BETA_RELEASE_AZURE_CLIENT_SECRET = 'test-secret-never-log'
            $log = Join-Path $script:TestRoot 'child.log'
            Invoke-ReleaseTool (Get-Process -Id $PID).Path @('-NoProfile', '-Command', 'if ($env:WINO_BETA_RELEASE_AZURE_CLIENT_SECRET -or $env:WINO_OPENAI_API_KEY -or $env:WINO_AI_OPENAI_TEST_KEY -or $env:WINO_BETA_RELEASE_SIGNING_ACCOUNT_NAME) { exit 7 }; Write-Output ''literal | $()''') $log
            Assert-True ((Get-Content -LiteralPath $log -Raw).Contains('literal | $()')) 'Shell interpreted a literal argument.'
            Assert-True (-not (Get-Content -LiteralPath $log -Raw).Contains($env:WINO_BETA_RELEASE_AZURE_CLIENT_SECRET)) 'Secret entered the log.'
            Assert-Throws { Invoke-ReleaseTool (Get-Process -Id $PID).Path @('-NoProfile', '-Command', 'exit 9') (Join-Path $script:TestRoot 'failure.log') } 'exit code 9'
        }
        finally { $env:WINO_BETA_RELEASE_AZURE_CLIENT_SECRET = $oldSecret }
    }
    Test-Case 'Store-only does not ask for signing configuration' {
        $plan = New-FixturePlan $true $false
        function Read-ReleaseSelection { return $plan.Selection }
        function New-ReleasePlan { param($Selection); return $plan }
        function Get-ReleaseTools { param($Selection); return @{} }
        function Get-ReleaseSigningConfiguration { throw 'Store must not require signing.' }
        function Invoke-ReleaseBuild { param($Plan, $Tools, $Signing); Assert-True ($null -eq $Signing) 'Unexpected credentials.' }
        function Invoke-Item { param($LiteralPath) }
        Invoke-InteractiveRelease
    }
    Test-Case 'Incomplete beta credentials fail before compilation' {
        $oldTenant = $env:WINO_BETA_RELEASE_AZURE_TENANT_ID
        try {
            $env:WINO_BETA_RELEASE_AZURE_TENANT_ID = $null
            Assert-Throws { Get-ReleaseSigningConfiguration } 'WINO_BETA_RELEASE_AZURE_TENANT_ID'
        }
        finally { $env:WINO_BETA_RELEASE_AZURE_TENANT_ID = $oldTenant }
    }
    Test-Case 'Stable-only selection requires the shared signing settings' {
        $plan = New-FixturePlan $false $false -Sideload $true
        function Read-ReleaseSelection { return $plan.Selection }
        function New-ReleasePlan { param($Selection); return $plan }
        function Get-ReleaseTools { param($Selection); return @{} }
        function Get-ReleaseSigningConfiguration {
            param([switch]$IncludeBeta, [switch]$IncludeSideload)
            Assert-True ($IncludeSideload -and -not $IncludeBeta) 'Wrong signing channels.'
            throw 'Missing shared signing setting.'
        }
        function Invoke-ReleaseBuild { throw 'Build must not start.' }
        Assert-Throws { Invoke-InteractiveRelease } 'Missing shared signing setting'
    }
    Test-Case 'Stable destination collision stops planning' {
        $plan = New-FixturePlan $false $false -Sideload $true
        $null = New-Item -ItemType Directory -Path $plan.Destinations[0] -Force
        Assert-Throws { New-ReleasePlan $plan.Selection $plan.RepositoryRoot } 'already exists'
    }
    Test-Case 'Missing signing setting fails without a configuration file fallback' {
        $previous = $env:WINO_BETA_RELEASE_SIGNING_ENDPOINT
        try {
            $env:WINO_BETA_RELEASE_SIGNING_ENDPOINT = $null
            Assert-Throws { Get-ReleaseSigningConfiguration } 'WINO_BETA_RELEASE_SIGNING_ENDPOINT'
        }
        finally { $env:WINO_BETA_RELEASE_SIGNING_ENDPOINT = $previous }
    }
    Test-Case 'Unsafe payload paths and absent architecture exports fail closed' {
        Assert-Throws { Resolve-ReleaseChildPath $script:TestRoot '../escape.exe' } 'within'
        Assert-Throws { Resolve-ReleaseChildPath $script:TestRoot 'C:\escape.exe' } 'within'
        $plan = New-FixturePlan $false $true
        Assert-Throws { New-SideloadPackage $plan @{} $script:TestRoot 'ARM64' } 'did not export'
    }
    Test-Case 'Wino credential names map to Azure names only for signing' {
        $plan = New-FixturePlan $false $true
        $directory = Join-Path $plan.RepositoryRoot 'scripts'
        $null = New-Item -ItemType Directory -Path $directory
        $dlib = Join-Path $directory 'test-dlib.dll'
        'fixture' | Set-Content -LiteralPath $dlib
        $values = @{
            WINO_BETA_RELEASE_AZURE_TENANT_ID = 'test-tenant'
            WINO_BETA_RELEASE_AZURE_CLIENT_ID = 'test-client'
            WINO_BETA_RELEASE_AZURE_CLIENT_SECRET = 'test-wino-secret'
            WINO_BETA_RELEASE_SIGNING_DLIB_PATH = $dlib
            WINO_BETA_RELEASE_APPINSTALLER_URI = 'https://example.com/WinoMailBeta.appinstaller'
            WINO_BETA_RELEASE_PACKAGE_BASE_URI = 'https://example.com/packages/'
        }
        $previous = @{}
        try {
            foreach ($key in $values.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $values[$key]) }
            $signing = Get-ReleaseSigningConfiguration -IncludeBeta -IncludeSideload
            Assert-True ($signing.Configuration.CodeSigningAccountName -eq 'test-account') 'Signing settings did not come from the environment.'
            Assert-True ($signing.Distributions.Beta.PackageBaseUri.AbsoluteUri -eq 'https://example.com/packages/') 'Distribution settings did not come from the environment.'
            Assert-True ($signing.Distributions.Sideload.AppInstallerUri.AbsoluteUri -eq 'http://download.winomail.app/WinoMail.appinstaller') 'Stable feed default is incorrect.'
            $env:WINO_BETA_RELEASE_APPINSTALLER_URI = 'invalid-beta-url'
            $null = Get-ReleaseSigningConfiguration -IncludeSideload
            $env:WINO_BETA_RELEASE_APPINSTALLER_URI = 'http://download.winomail.app/WinoMail.appinstaller'
            Assert-Throws { Get-ReleaseSigningConfiguration -IncludeBeta -IncludeSideload } 'different App Installer'
            Assert-True ($signing.Credentials.AZURE_CLIENT_SECRET -ceq 'test-wino-secret') 'Secret did not map to EnvironmentCredential.'
            Assert-True ($signing.Credentials.Count -eq 3) 'Unexpected signing environment.'
            $log = Join-Path $script:TestRoot 'signing-child.log'
            Invoke-ReleaseTool (Get-Process -Id $PID).Path @('-NoProfile', '-Command', 'if ($env:WINO_BETA_RELEASE_AZURE_CLIENT_SECRET) { exit 7 }; Write-Output $env:AZURE_CLIENT_SECRET') $log $signing.Credentials
            Assert-True ((Get-Content -LiteralPath $log -Raw).Contains('[redacted]')) 'Signing output was not redacted.'
            Assert-True (-not (Get-Content -LiteralPath $log -Raw).Contains('test-wino-secret')) 'Secret appeared in the log.'
        }
        finally { foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) } }
    }
    Test-Case 'PRI comparison includes embedded XAML bytes and verifies asset paths' {
        $layout = Join-Path $script:TestRoot 'resource-fixture'
        $null = New-Item -ItemType Directory -Path $layout
        'asset' | Set-Content -LiteralPath (Join-Path $layout 'image.png')
        $xml = [xml]'<PriInfo><ResourceMap name="Store"><NamedResource uri="ms-resource://Store/Files/a.xbf"><Candidate type="EmbeddedData"><QualifierSet /><Base64Value>AQID</Base64Value></Candidate></NamedResource><NamedResource uri="ms-resource://Store/Files/image.png"><Candidate type="Path"><QualifierSet /><Value>image.png</Value></Candidate></NamedResource></ResourceMap></PriInfo>'
        $before = @(Get-PriCandidates $xml $layout)
        $after = @(Get-PriCandidates ([xml]$xml.OuterXml.Replace('Store', 'Beta')) $layout)
        Assert-True (@(Compare-Object $before $after).Count -eq 0) 'Resource-map rename changed candidates.'
        $changed = @(Get-PriCandidates ([xml]$xml.OuterXml.Replace('AQID', 'AQIE')) $layout)
        Assert-True (@(Compare-Object $before $changed).Count -gt 0) 'Embedded resource corruption was ignored.'
        Assert-Throws { Get-PriCandidates ([xml]$xml.OuterXml.Replace('image.png', 'missing.png')) $layout } 'missing asset'
    }
    Test-Case 'Website defaults and URL validation' {
        $configuration = Get-ReleaseDistributionConfiguration @{}
        Assert-True ($configuration.AppInstallerUri.AbsoluteUri -eq 'http://download.winomail.app/WinoMailBeta.appinstaller') 'Wrong stable feed URL.'
        Assert-True ($configuration.PackageBaseUri.AbsoluteUri -eq 'http://download.winomail.app/') 'Wrong package base.'
        Assert-Throws { Get-ReleaseDistributionConfiguration @{ AppInstallerUri = 'ftp://example.com/WinoMailBeta.appinstaller' } } 'HTTP or HTTPS'
        Assert-True ((Get-ReleaseDistributionConfiguration @{ AppInstallerUri = 'https://example.com/WinoMailBeta.appinstaller' }).AppInstallerUri.Scheme -eq 'https') 'HTTPS override was rejected.'
        Assert-Throws { Get-ReleaseDistributionConfiguration @{ PackageBaseUri = 'https://example.com/files' } } 'slash'
        Assert-Throws { Get-ReleaseDistributionConfiguration @{ AppInstallerUri = 'https://example.com/Wino.msix' } } 'filename'
        Assert-True ((Get-ReleaseDownloadUri ([uri]'https://example.com/') 'Beta/with space.msix') -eq 'https://example.com/Beta/with%20space.msix') 'URL segments were not escaped.'
    }
    Test-Case 'App Installer uses package identity, versioned URLs, dependencies, and automatic updates' {
        $plan = New-FixturePlan $false $true
        $folder = Join-Path $script:TestRoot 'WinoMail_Beta_2.53.0.0'
        $bundleSource = Join-Path $script:TestRoot 'bundle-fixture'
        $null = New-Item -ItemType Directory -Path (Join-Path $bundleSource 'AppxMetadata') -Force
        $null = New-Item -ItemType Directory -Path $folder -Force
        $bundleXml = [xml]'<Bundle><Identity /></Bundle>'
        $bundleIdentity = $bundleXml.SelectSingleNode('/Bundle/Identity')
        $bundleIdentity.SetAttribute('Name', $script:SideloadIdentityName)
        $bundleIdentity.SetAttribute('Publisher', $script:SideloadPublisher)
        $bundleIdentity.SetAttribute('Version', $plan.Version)
        $bundleXml.Save((Join-Path $bundleSource 'AppxMetadata/AppxBundleManifest.xml'))
        $bundle = Join-Path $folder 'WinoMail_Beta_2.53.0.0.msixbundle'
        [IO.Compression.ZipFile]::CreateFromDirectory($bundleSource, $bundle)
        $depSource = Join-Path $script:TestRoot 'dependency-fixture'
        $null = New-Item -ItemType Directory -Path $depSource -Force
        '<Package><Identity Name="Test.Framework" Publisher="CN=Test &amp; Co" Version="1.0.0.0" ProcessorArchitecture="x64" /></Package>' |
            Set-Content -LiteralPath (Join-Path $depSource 'AppxManifest.xml')
        $null = New-Item -ItemType Directory -Path (Join-Path $folder 'Dependencies/x64') -Force
        [IO.Compression.ZipFile]::CreateFromDirectory($depSource, (Join-Path $folder 'Dependencies/x64/framework.msix'))
        New-SideloadAppInstaller $bundle $plan (Get-ReleaseDistributionConfiguration @{})
        $xml = [xml](Get-Content -LiteralPath (Join-Path $folder 'WinoMailBeta.appinstaller') -Raw)
        Assert-True ($xml.AppInstaller.Uri -eq 'http://download.winomail.app/WinoMailBeta.appinstaller') 'Wrong beta feed URL.'
        Assert-True ($xml.AppInstaller.MainBundle.Publisher -ceq $script:SideloadPublisher) 'Publisher Unicode changed.'
        Assert-True ($xml.AppInstaller.MainBundle.Uri -eq 'http://download.winomail.app/WinoMail_Beta_2.53.0.0/WinoMail_Beta_2.53.0.0.msixbundle') 'Wrong bundle URL.'
        Assert-True ($xml.AppInstaller.Dependencies.Package.Publisher -eq 'CN=Test & Co') 'Dependency XML escaping failed.'
        Assert-True ($xml.AppInstaller.Dependencies.Package.Uri.EndsWith('/Dependencies/x64/framework.msix')) 'Wrong dependency URL.'
        Assert-True ($xml.AppInstaller.UpdateSettings.OnLaunch.HoursBetweenUpdateChecks -eq '4') 'Wrong update interval.'
        Assert-True ($xml.DocumentElement.NamespaceURI -eq 'http://schemas.microsoft.com/appx/appinstaller/2017/2') 'Schema exceeds the minimum supported OS.'
        $stableFolder = Join-Path $script:TestRoot 'WinoMail_SideloadRelease_2.53.0'
        $null = New-Item -ItemType Directory -Path $stableFolder
        $stableBundle = Join-Path $stableFolder 'WinoMail_SideloadRelease_2.53.0.msixbundle'
        Copy-Item -LiteralPath $bundle -Destination $stableBundle
        New-SideloadAppInstaller $stableBundle $plan (Get-ReleaseDistributionConfiguration @{ AppInstallerUri = 'http://download.winomail.app/WinoMail.appinstaller' })
        $stableXml = [xml](Get-Content -LiteralPath (Join-Path $stableFolder 'WinoMail.appinstaller') -Raw)
        Assert-True ($stableXml.AppInstaller.Uri -eq 'http://download.winomail.app/WinoMail.appinstaller') 'Wrong official feed URL.'
        Assert-True ($stableXml.AppInstaller.MainBundle.Version -eq '2.53.0.0') 'Stable package version lost its revision.'
        Assert-True ($stableXml.AppInstaller.MainBundle.Uri -eq 'http://download.winomail.app/WinoMail_SideloadRelease_2.53.0/WinoMail_SideloadRelease_2.53.0.msixbundle') 'Stable feed has the wrong bundle URL.'
    }
    foreach ($mask in 1..7) {
      foreach ($architectures in @(@('x64'), @('x86', 'x64', 'ARM64'))) {
        Test-Case "One build and signing operation: channels=$mask, architectures=$($architectures -join ',')" {
            $plan = New-FixturePlan ([bool]($mask -band 1)) ([bool]($mask -band 2)) $architectures ([bool]($mask -band 4))
            $script:Commands = [Collections.Generic.List[string]]::new()
            $script:PackageCalls = 0
            $script:SignCalls = 0
            function Invoke-ReleaseTool {
                param($Executable, $Arguments, $LogPath, $SigningEnvironment)
                $script:Commands.Add($Arguments -join ' ')
            }
            function Get-StoreReleaseArtifact {
                param($Plan, $Staging)
                $folder = Join-Path $Staging "ready/WinoMail_Store_$($Plan.Version)"
                $null = New-Item -ItemType Directory -Path $folder -Force
                'upload' | Set-Content -LiteralPath (Join-Path $folder 'store.msixupload')
                return @{ 'x64/app.exe' = 'hash' }
            }
            function New-SideloadPackage { param($Plan, $Tools, $Staging, $Architecture); $script:PackageCalls++ }
            function Assert-ReleaseBundle { param($Bundle, $Plan, $Name, $Publisher, $InspectionRoot); return @{ 'x64/app.exe' = 'hash' } }
            function Copy-ReleaseDependencies { param($SdkOutput, $Destination) }
            function Sign-SideloadRelease { param($Bundle, $Plan, $Tools, $Signing, $Staging); $script:SignCalls++; 'signed' | Set-Content -LiteralPath $Bundle }
            function New-SideloadAppInstaller { param($Bundle, $Plan, $Distribution) }
            Invoke-ReleaseBuild $plan ([pscustomobject]@{ MSBuild = 'dotnet'; MakeAppx = 'makeappx' }) @{ Distributions = @{ Beta = @{}; Sideload = @{} } }
            Assert-True (@($script:Commands | Where-Object { $_ -match '-t:Build' }).Count -eq 1) 'Compilation repeated.'
            Assert-True (@($script:Commands | Where-Object { $_ -match '-t:Restore' }).Count -eq 1) 'Restore repeated.'
            $expectedMode = if ($plan.Selection.Store) { 'StoreUpload' } else { 'SideloadOnly' }
            Assert-True (@($script:Commands | Where-Object { $_ -match "-p:UapAppxPackageBuildMode=$expectedMode" }).Count -eq 1) 'Wrong SDK packaging mode.'
            foreach ($destination in $plan.Destinations) { Assert-True (Test-Path -LiteralPath $destination) 'Missing selected output.' }
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $plan.OutputRoot '.staging'))) 'Successful run retained staging.'
            $expectedSigns = if ($mask -band 6) { 1 } else { 0 }
            Assert-True ($script:SignCalls -eq $expectedSigns) 'Signing repeated or was skipped.'
            Assert-True ($script:PackageCalls -eq ($expectedSigns * $architectures.Count)) 'Sideload architecture packaging repeated.'
            Assert-True (@(Get-ChildItem -LiteralPath $plan.OutputRoot -Directory).Count -eq $plan.Destinations.Count) 'Unselected channel folder exists.'
            if ($plan.Selection.Sideload) {
                $stableBundle = Join-Path $plan.OutputRoot 'WinoMail_SideloadRelease_2.53.0/WinoMail_SideloadRelease_2.53.0.msixbundle'
                Assert-True (Test-Path -LiteralPath $stableBundle) 'Wrong stable output name.'
                if ($plan.Selection.Beta) {
                    $betaBundle = Join-Path $plan.OutputRoot 'WinoMail_Beta_2.53.0.0/WinoMail_Beta_2.53.0.0.msixbundle'
                    Assert-True ((Get-FileHash $stableBundle).Hash -ceq (Get-FileHash $betaBundle).Hash) 'Signed channel copies differ.'
                }
            }
        }
      }
    }
    Test-Case 'Staging cleanup preserves earlier diagnostics and rejects paths outside staging' {
        $plan = New-FixturePlan $true $false
        $staging = Join-Path $plan.OutputRoot ('.staging/' + [guid]::NewGuid().ToString('N'))
        $previous = Join-Path $plan.OutputRoot '.staging/previous-failure'
        $null = New-Item -ItemType Directory -Path $staging, $previous -Force
        'diagnostics' | Set-Content -LiteralPath (Join-Path $previous 'build.log')
        Assert-Throws { Remove-ReleaseStaging $plan $plan.OutputRoot } 'current release run directory'
        Remove-ReleaseStaging $plan $staging
        Assert-True (-not (Test-Path -LiteralPath $staging)) 'Current run was retained.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $previous 'build.log')) -eq 'diagnostics') 'Earlier diagnostics were removed.'
    }
    Test-Case 'Signing failure exposes neither output and preserves staging' {
        $plan = New-FixturePlan $true $true -Sideload $true
        function Invoke-ReleaseTool { param($Executable, $Arguments, $LogPath, $SigningEnvironment) }
        function Get-StoreReleaseArtifact {
            param($Plan, $Staging)
            $null = New-Item -ItemType Directory -Path (Join-Path $Staging "ready/WinoMail_Store_$($Plan.Version)") -Force
            return @{}
        }
        function New-SideloadPackage { param($Plan, $Tools, $Staging, $Architecture) }
        function Assert-ReleaseBundle { param($Bundle, $Plan, $Name, $Publisher, $InspectionRoot); return @{} }
        function Copy-ReleaseDependencies { param($SdkOutput, $Destination) }
        function Sign-SideloadRelease { param($Bundle, $Plan, $Tools, $Signing, $Staging); throw 'Signing denied.' }
        Assert-Throws { Invoke-ReleaseBuild $plan ([pscustomobject]@{ MSBuild = 'dotnet'; MakeAppx = 'makeappx' }) @{} } 'Azure signing'
        foreach ($destination in $plan.Destinations) { Assert-True (-not (Test-Path -LiteralPath $destination)) 'Incomplete release escaped staging.' }
        Assert-True (Test-Path -LiteralPath (Join-Path $plan.OutputRoot '.staging')) 'Diagnostics were removed.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $plan.OutputRoot '.release.lock'))) 'Lock was not released.'
    }
    Test-Case 'Finalization rolls back both channels if the third move fails' {
        $plan = New-FixturePlan $true $true -Sideload $true
        $staging = Join-Path $plan.OutputRoot '.staging/test'
        $ready = Join-Path $staging 'ready/WinoMail_Store_2.53.0.0'
        $betaReady = Join-Path $staging 'ready/WinoMail_Beta_2.53.0.0'
        $null = New-Item -ItemType Directory -Path $ready, $betaReady -Force
        Assert-Throws { Complete-ReleaseOutputs $plan $staging } 'Could not find|cannot find'
        Assert-True (Test-Path -LiteralPath $ready) 'First channel was not restored.'
        Assert-True (Test-Path -LiteralPath $betaReady) 'Second channel was not restored.'
        foreach ($destination in $plan.Destinations) { Assert-True (-not (Test-Path -LiteralPath $destination)) 'Partial finalization remained.' }
    }
    Write-Host "$script:Passed release script tests passed." -ForegroundColor Green
}
finally {
    foreach ($key in $script:SavedSettings.Keys) { [Environment]::SetEnvironmentVariable($key, $script:SavedSettings[$key]) }
    $resolvedRoot = [IO.Path]::GetFullPath($script:TestRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('wino-release-tests-')) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
