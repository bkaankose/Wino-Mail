<#
.SYNOPSIS
Builds and manages the single-VM Exchange Server SE lab (wino-exchange) in Hyper-V.
.EXAMPLE
.\scripts\lab\windows\exchange-lab.ps1 create
.EXAMPLE
.\scripts\lab\windows\exchange-lab.ps1 trust
.EXAMPLE
.\scripts\lab\windows\exchange-lab.ps1 restore
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('create', 'status', 'start', 'stop', 'checkpoint', 'restore', 'trust', 'untrust', 'verify', 'remove', 'help', 'hosts', 'unhosts', 'mailflow')]
    [string]$Command = 'help',
    [string]$IsoRoot = 'D:\Hyper-V\iso',
    [string]$VmRoot = 'D:\Hyper-V',
    [ValidateSet('vm', 'guest', 'forest', 'domain', 'exchange', 'configure', 'mailflow')]
    [string]$StartAt = 'vm',
    [switch]$Force,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$labRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../tools/exchange-lab'))
$stateRoot = Join-Path $labRoot '.state'
$vmName = 'wino-exchange'
$switchName = 'WinoExchangeLab'
$natName = 'WinoExchangeLabNat'
$hostIp = '192.168.250.1'
$vmIp = '192.168.250.10'
$prefix = '192.168.250.0/24'
$domain = 'exchange.test'
# Exchange transport rejects recipients in RFC 2606 TLDs (501 5.1.4), so mail uses .internal while AD stays on .test.
$mailDomain = 'exchange.internal'
$netbios = 'EXCHANGE'
$computerName = 'EX1'
$password = '1'
# Install-ADDSForest rejects a DSRM password that fails complexity, even in a lab.
$dsrmPassword = 'Wino-Lab-Dsrm-2026!'
$mailboxes = @('alice', 'bob', 'carol')
$hostNames = @("ex1.$domain", "mail.$mailDomain", "autodiscover.$mailDomain", $mailDomain)
$hostsMarker = '# wino-exchange-lab'
$checkpointName = 'exchange-installed'
$exchangeIso = Join-Path $IsoRoot 'ExchangeServerSE-x64.iso'
$serverIso = Join-Path $IsoRoot 'WindowsServer2025-Eval.iso'
$certificatePath = Join-Path $stateRoot 'wino-exchange.cer'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-SelfElevated {
    param([string]$Name)
    New-Item -ItemType Directory -Force $stateRoot | Out-Null
    $log = Join-Path $stateRoot "$Name.log"
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, $Name, '-IsoRoot', $IsoRoot, '-VmRoot', $VmRoot, '-StartAt', $StartAt, '-Elevated')
    if ($Force) { $arguments += '-Force' }
    Write-Output "Running '$Name' elevated (UAC prompt). Log: $log"
    $process = Start-Process pwsh -Verb RunAs -ArgumentList $arguments -PassThru -Wait
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 40 }
    if ($process.ExitCode -ne 0) { throw "Elevated '$Name' failed ($($process.ExitCode)). See $log" }
}

function Write-Step { param([string]$Text) Write-Output "[$(Get-Date -Format HH:mm:ss)] $Text" }

function Get-Credential1 {
    param([string]$User)
    [pscredential]::new($User, (ConvertTo-SecureString $password -AsPlainText -Force))
}

function Get-LocalCredential { Get-Credential1 'Administrator' }
function Get-DomainCredential { Get-Credential1 "$netbios\Administrator" }

function Wait-Guest {
    param([pscredential]$Credential, [int]$TimeoutMinutes = 30, [scriptblock]$Probe = { $true }, [int]$IntervalSeconds = 15)
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        try {
            if (Invoke-Command -VMName $vmName -Credential $Credential -ScriptBlock $Probe -ErrorAction Stop) { return }
        } catch { }
        Start-Sleep -Seconds $IntervalSeconds
    }
    throw "The guest did not answer PowerShell Direct within $TimeoutMinutes minutes."
}

function Restart-Guest {
    param([pscredential]$Credential, [scriptblock]$Probe = { $true }, [int]$TimeoutMinutes = 30)
    Restart-LabVm
    Wait-Guest -Credential $Credential -Probe $Probe -TimeoutMinutes $TimeoutMinutes -IntervalSeconds 30
}

function Restart-LabVm {
    # A Restart-Computer sent over PowerShell Direct can be lost (the session drops first); the host-side shutdown always happens.
    Stop-VM -Name $vmName -Force
    Start-VM -Name $vmName
    Start-Sleep -Seconds 60
}

function Invoke-Guest {
    param([pscredential]$Credential, [scriptblock]$ScriptBlock, [object[]]$ArgumentList = @())
    Invoke-Command -VMName $vmName -Credential $Credential -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList -ErrorAction Stop
}

function Initialize-Network {
    if (!(Get-VMSwitch -Name $switchName -ErrorAction SilentlyContinue)) {
        Write-Step "Creating internal switch $switchName"
        New-VMSwitch -Name $switchName -SwitchType Internal | Out-Null
    }
    $alias = "vEthernet ($switchName)"
    if (!(Get-NetIPAddress -InterfaceAlias $alias -IPAddress $hostIp -ErrorAction SilentlyContinue)) {
        New-NetIPAddress -InterfaceAlias $alias -IPAddress $hostIp -PrefixLength 24 | Out-Null
    }
    if (!(Get-NetNat -Name $natName -ErrorAction SilentlyContinue)) {
        $other = @(Get-NetNat -ErrorAction SilentlyContinue)
        if ($other.Count -gt 0) { Write-Step "Existing NAT networks: $($other.Name -join ', ') ($($other.InternalIPInterfaceAddressPrefix -join ', '))" }
        Write-Step "Creating NAT $natName for $prefix"
        New-NetNat -Name $natName -InternalIPInterfaceAddressPrefix $prefix | Out-Null
    }
}

function New-UnattendXml {
    $timeZone = (Get-TimeZone).Id
    @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>$computerName</ComputerName>
      <TimeZone>$timeZone</TimeZone>
      <RegisteredOwner>Wino Lab</RegisteredOwner>
    </component>
    <component name="Microsoft-Windows-ServerManager-SvrMgrNc" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <DoNotOpenServerManagerAtLogon>true</DoNotOpenServerManagerAtLogon>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>en-US</InputLocale>
      <SystemLocale>en-US</SystemLocale>
      <UILanguage>en-US</UILanguage>
      <UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <UserAccounts>
        <AdministratorPassword><Value>$password</Value><PlainText>true</PlainText></AdministratorPassword>
      </UserAccounts>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
        <SkipMachineOOBE>true</SkipMachineOOBE>
      </OOBE>
    </component>
  </settings>
</unattend>
"@
}

function New-SystemDisk {
    param([string]$VhdPath)
    Write-Step 'Mounting the Windows Server ISO'
    $iso = Mount-DiskImage -ImagePath $serverIso -PassThru
    try {
        $isoLetter = ($iso | Get-Volume).DriveLetter
        $wim = "${isoLetter}:\sources\install.wim"
        $image = Get-WindowsImage -ImagePath $wim | Where-Object { $_.ImageName -like '*Datacenter*Desktop Experience*' } | Select-Object -First 1
        if (!$image) { throw "No Datacenter (Desktop Experience) image in $wim" }
        Write-Step "Applying $($image.ImageName) to $VhdPath"
        New-VHD -Path $VhdPath -SizeBytes 150GB -Dynamic | Out-Null
        $disk = Mount-VHD -Path $VhdPath -Passthru | Get-Disk
        try {
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT
            $efi = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -AssignDriveLetter
            Format-Volume -Partition $efi -FileSystem FAT32 -NewFileSystemLabel System -Confirm:$false | Out-Null
            New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
            $os = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
            Format-Volume -Partition $os -FileSystem NTFS -NewFileSystemLabel Windows -Confirm:$false | Out-Null
            $efiLetter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber).DriveLetter
            $osLetter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $os.PartitionNumber).DriveLetter
            Expand-WindowsImage -ImagePath $wim -Index $image.ImageIndex -ApplyPath "${osLetter}:\" | Out-Null
            & "$env:SystemRoot\System32\bcdboot.exe" "${osLetter}:\Windows" /s "${efiLetter}:" /f UEFI
            if ($LASTEXITCODE -ne 0) { throw "bcdboot failed ($LASTEXITCODE)" }
            New-Item -ItemType Directory -Force "${osLetter}:\Windows\Panther" | Out-Null
            Set-Content -LiteralPath "${osLetter}:\Windows\Panther\unattend.xml" -Value (New-UnattendXml) -Encoding UTF8
            Remove-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber -AccessPath "${efiLetter}:\"
        } finally { Dismount-VHD -Path $VhdPath }
    } finally { Dismount-DiskImage -ImagePath $serverIso | Out-Null }
}

function New-LabVm {
    $vmPath = Join-Path $VmRoot $vmName
    $vhdPath = Join-Path $vmPath "$vmName.vhdx"
    New-Item -ItemType Directory -Force $vmPath | Out-Null
    if (!(Test-Path -LiteralPath $vhdPath)) { New-SystemDisk -VhdPath $vhdPath }
    Write-Step "Creating VM $vmName"
    New-VM -Name $vmName -Generation 2 -MemoryStartupBytes 10GB -VHDPath $vhdPath -SwitchName $switchName -Path $VmRoot | Out-Null
    # Exchange does not support dynamic memory.
    Set-VMMemory -VMName $vmName -DynamicMemoryEnabled $false
    Set-VMProcessor -VMName $vmName -Count 4
    Set-VM -Name $vmName -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -CheckpointType Standard
    Add-VMDvdDrive -VMName $vmName -Path $exchangeIso
    Set-VMFirmware -VMName $vmName -FirstBootDevice (Get-VMHardDiskDrive -VMName $vmName)
    Enable-VMIntegrationService -VMName $vmName -Name 'Guest Service Interface'
    Start-VM -Name $vmName
}

function Initialize-Guest {
    $local = Get-LocalCredential
    Write-Step 'Waiting for Windows setup to finish'
    Wait-Guest -Credential $local -TimeoutMinutes 40
    Write-Step 'Configuring network, prerequisites and Windows features'
    Invoke-Guest -Credential $local -ArgumentList $vmIp, $hostIp -ScriptBlock {
        param($ip, $gateway)
        $ErrorActionPreference = 'Stop'
        $adapter = Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object -First 1
        if (!(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $ip -ErrorAction SilentlyContinue)) {
            Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -Dhcp Disabled
            Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false
            New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $ip -PrefixLength 24 -DefaultGateway $gateway | Out-Null
        }
        Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses '1.1.1.1', '8.8.8.8'
        Set-NetConnectionProfile -InterfaceIndex $adapter.ifIndex -NetworkCategory Private -ErrorAction SilentlyContinue
        Set-Service RemoteRegistry -StartupType Automatic
        Start-Service RemoteRegistry
        Enable-NetFirewallRule -DisplayGroup 'File and Printer Sharing' -ErrorAction SilentlyContinue
        # Lets the host ping the VM.
        Set-NetFirewallRule -Name 'FPS-ICMP4-ERQ-In*' -Enabled True -ErrorAction SilentlyContinue

        $downloads = 'C:\LabSetup'
        New-Item -ItemType Directory -Force $downloads | Out-Null
        $ProgressPreference = 'SilentlyContinue'
        $packages = @(
            @{ Name = 'vcredist2012_x64.exe'; Url = 'https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe'; Args = '/install /quiet /norestart' },
            @{ Name = 'vcredist2013_x64.exe'; Url = 'https://aka.ms/highdpimfc2013x64enu'; Args = '/install /quiet /norestart' },
            @{ Name = 'rewrite_amd64_en-US.msi'; Url = 'https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi'; Args = $null }
        )
        $features = 'Server-Media-Foundation', 'NET-Framework-45-Core', 'NET-Framework-45-ASPNET', 'NET-WCF-HTTP-Activation45', 'NET-WCF-Pipe-Activation45',
            'NET-WCF-TCP-Activation45', 'NET-WCF-TCP-PortSharing45', 'RPC-over-HTTP-proxy', 'RSAT-Clustering', 'RSAT-Clustering-CmdInterface', 'RSAT-Clustering-Mgmt',
            'RSAT-Clustering-PowerShell', 'WAS-Process-Model', 'Web-Asp-Net45', 'Web-Basic-Auth', 'Web-Client-Auth', 'Web-Digest-Auth', 'Web-Dir-Browsing',
            'Web-Dyn-Compression', 'Web-Http-Errors', 'Web-Http-Logging', 'Web-Http-Redirect', 'Web-Http-Tracing', 'Web-ISAPI-Ext', 'Web-ISAPI-Filter',
            'Web-Metabase', 'Web-Mgmt-Console', 'Web-Mgmt-Service', 'Web-Net-Ext45', 'Web-Request-Monitor', 'Web-Server', 'Web-Stat-Compression',
            'Web-Static-Content', 'Web-Windows-Auth', 'Web-WMI', 'Windows-Identity-Foundation', 'RSAT-ADDS', 'AD-Domain-Services', 'DNS', 'RSAT-DNS-Server'
        Install-WindowsFeature -Name $features | Out-Null
        foreach ($package in $packages) {
            $path = Join-Path $downloads $package.Name
            if (!(Test-Path $path)) { Invoke-WebRequest -Uri $package.Url -OutFile $path -UseBasicParsing }
            if ($package.Args) { Start-Process $path -ArgumentList $package.Args -Wait }
            else { Start-Process msiexec.exe -ArgumentList "/i `"$path`" /quiet /norestart" -Wait }
        }
        $dvd = (Get-Volume | Where-Object DriveType -eq 'CD-ROM' | Select-Object -First 1).DriveLetter
        $ucma = Get-ChildItem "${dvd}:\UCMARedist" -Filter *.exe | Where-Object Name -match 'Setup|UCMA' | Select-Object -First 1
        Start-Process $ucma.FullName -ArgumentList '-q' -Wait
    }
    Write-Step 'Restarting after prerequisites'
    Restart-Guest -Credential $local
}

function Install-Forest {
    $local = Get-LocalCredential
    Write-Step "Promoting $computerName to the first domain controller of $domain"
    Invoke-Guest -Credential $local -ArgumentList $domain, $netbios, $dsrmPassword -ScriptBlock {
        param($domain, $netbios, $dsrm)
        # Server 2025 locks out Administrator after failed logons; the host's readiness probes count as failures while AD starts.
        net accounts /lockoutthreshold:0 | Out-Null
        Import-Module ADDSDeployment
        $result = Install-ADDSForest -DomainName $domain -DomainNetbiosName $netbios -InstallDns -SafeModeAdministratorPassword (ConvertTo-SecureString $dsrm -AsPlainText -Force) -NoRebootOnCompletion -Force -WarningAction SilentlyContinue
        if ($result.Status -ne 'Success') { throw "Install-ADDSForest: $($result.Status) $($result.Message)" }
    }
    # After promotion the local Administrator becomes EXCHANGE\Administrator, so only the domain credential answers.
    Restart-LabVm
    Wait-DomainReady
}

function Wait-DomainReady {
    Write-Step 'Waiting for Active Directory'
    Start-Sleep -Seconds 180
    Wait-Guest -Credential (Get-DomainCredential) -TimeoutMinutes 30 -IntervalSeconds 60 -Probe { try { [bool](Get-ADDomain) } catch { $false } }
    Invoke-Guest -Credential (Get-DomainCredential) -ArgumentList $vmIp -ScriptBlock {
        param($ip)
        Set-ADDefaultDomainPasswordPolicy -Identity (Get-ADDomain).DNSRoot -ComplexityEnabled $false -MinPasswordLength 0 -PasswordHistoryCount 0 -MinPasswordAge 0 -MaxPasswordAge 0 -LockoutThreshold 0
        $adapter = Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object -First 1
        Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses '127.0.0.1'
        if (!(Get-DnsServerForwarder).IPAddress) { Add-DnsServerForwarder -IPAddress 1.1.1.1, 8.8.8.8 }
        gpupdate /force | Out-Null
    }
}

function Install-Exchange {
    $credential = Get-DomainCredential
    Write-Step 'Running Exchange setup (PrepareSchema, PrepareAD, Mailbox role). This takes 40-70 minutes.'
    Invoke-Guest -Credential $credential -ScriptBlock {
        $ErrorActionPreference = 'Stop'
        $dvd = (Get-Volume | Where-Object DriveType -eq 'CD-ROM' | Select-Object -First 1).DriveLetter
        $setup = "${dvd}:\Setup.exe"
        $license = '/IAcceptExchangeServerLicenseTerms_DiagnosticDataOFF'
        foreach ($arguments in @("$license /PrepareSchema", "$license /PrepareAD /OrganizationName:`"Wino Lab`"", "$license /Mode:Install /Roles:Mailbox /InstallWindowsComponents")) {
            if ($arguments -match 'Mode:Install' -and (Get-Service MSExchangeIS -ErrorAction SilentlyContinue)) { continue }
            $process = Start-Process $setup -ArgumentList $arguments -Wait -PassThru -NoNewWindow
            if ($process.ExitCode -ne 0) {
                Get-Content C:\ExchangeSetupLogs\ExchangeSetup.log -Tail 60
                throw "Exchange setup '$arguments' failed ($($process.ExitCode))"
            }
        }
    }
    Write-Step 'Restarting after Exchange setup'
    Restart-Guest -Credential $credential -TimeoutMinutes 30 -Probe { (Get-Service MSExchangeTransport, MSExchangeIS -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running').Count -eq 2 }
}

function Set-ExchangeLab {
    $credential = Get-DomainCredential
    Write-Step 'Configuring certificate, URLs, DNS records, transport and mailboxes'
    Invoke-Guest -Credential $credential -ArgumentList $mailDomain, $vmIp, $password, $mailboxes, $hostNames -ScriptBlock {
        param($mailDomain, $ip, $password, $mailboxes, $hostNames)
        $ErrorActionPreference = 'Stop'
        Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn
        $server = $env:COMPUTERNAME

        if (!(Get-DnsServerZone -Name $mailDomain -ErrorAction SilentlyContinue)) { Add-DnsServerPrimaryZone -Name $mailDomain -ReplicationScope Forest }
        foreach ($name in 'mail', 'autodiscover', '@') {
            if (!(Get-DnsServerResourceRecord -ZoneName $mailDomain -Name $name -RRType A -ErrorAction SilentlyContinue)) { Add-DnsServerResourceRecordA -ZoneName $mailDomain -Name $name -IPv4Address $ip }
        }
        if (!(Get-DnsServerResourceRecord -ZoneName $mailDomain -RRType Mx -ErrorAction SilentlyContinue)) { Add-DnsServerResourceRecordMX -ZoneName $mailDomain -Name '.' -MailExchange "mail.$mailDomain" -Preference 10 }
        if (!(Get-DnsServerResourceRecord -ZoneName $mailDomain -Name '_autodiscover._tcp' -ErrorAction SilentlyContinue)) {
            Add-DnsServerResourceRecord -ZoneName $mailDomain -Srv -Name '_autodiscover._tcp' -DomainName "autodiscover.$mailDomain" -Priority 0 -Weight 0 -Port 443
        }

        if (!(Get-AcceptedDomain -Identity $mailDomain -ErrorAction SilentlyContinue)) { New-AcceptedDomain -Name $mailDomain -DomainName $mailDomain -DomainType Authoritative | Out-Null }
        Set-AcceptedDomain -Identity $mailDomain -MakeDefault $true
        Set-EmailAddressPolicy -Identity 'Default Policy' -EnabledEmailAddressTemplates "SMTP:%m@$mailDomain"
        Update-EmailAddressPolicy -Identity 'Default Policy'
        if ((Get-ADForest).UPNSuffixes -notcontains $mailDomain) { Set-ADForest -Identity (Get-ADForest) -UPNSuffixes @{ Add = $mailDomain } }
        # The client connectors default to 5 messages a minute per user, which a test run of a mail client exceeds.
        Get-ReceiveConnector | Where-Object { $_.MessageRateLimit -ne 'Unlimited' } | Set-ReceiveConnector -MessageRateLimit Unlimited

        $certificate = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq "CN=mail.$mailDomain" | Sort-Object NotAfter -Descending | Select-Object -First 1
        if (!$certificate) {
            $san = (($hostNames | ForEach-Object { "DNS=$_" }) + "IPAddress=$ip") -join '&'
            $certificate = New-SelfSignedCertificate -Subject "CN=mail.$mailDomain" -TextExtension "2.5.29.17={text}$san" -CertStoreLocation Cert:\LocalMachine\My `
                -KeyExportPolicy Exportable -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5) `
                -Provider 'Microsoft Enhanced RSA and AES Cryptographic Provider' -KeyUsage DigitalSignature, KeyEncipherment
            Export-Certificate -Cert $certificate -FilePath C:\LabSetup\wino-exchange.cer | Out-Null
            Import-Certificate -FilePath C:\LabSetup\wino-exchange.cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        }
        Export-Certificate -Cert $certificate -FilePath C:\LabSetup\wino-exchange.cer -Force | Out-Null
        Enable-ExchangeCertificate -Server $server -Thumbprint $certificate.Thumbprint -Services IIS, SMTP -Force

        $base = "https://mail.$mailDomain"
        Get-WebServicesVirtualDirectory -Server $server | Set-WebServicesVirtualDirectory -InternalUrl "$base/EWS/Exchange.asmx" -ExternalUrl "$base/EWS/Exchange.asmx" -BasicAuthentication $true -WindowsAuthentication $true -Force
        Get-MapiVirtualDirectory -Server $server | Set-MapiVirtualDirectory -InternalUrl "$base/mapi" -ExternalUrl "$base/mapi" -IISAuthenticationMethods Ntlm, Negotiate, Basic
        Get-OabVirtualDirectory -Server $server | Set-OabVirtualDirectory -InternalUrl "$base/OAB" -ExternalUrl "$base/OAB"
        Get-OwaVirtualDirectory -Server $server | Set-OwaVirtualDirectory -InternalUrl "$base/owa" -ExternalUrl "$base/owa"
        Get-EcpVirtualDirectory -Server $server | Set-EcpVirtualDirectory -InternalUrl "$base/ecp" -ExternalUrl "$base/ecp"
        Get-ActiveSyncVirtualDirectory -Server $server | Set-ActiveSyncVirtualDirectory -InternalUrl "$base/Microsoft-Server-ActiveSync" -ExternalUrl "$base/Microsoft-Server-ActiveSync"
        Get-OutlookAnywhere -Server $server | Set-OutlookAnywhere -InternalHostname "mail.$mailDomain" -ExternalHostname "mail.$mailDomain" -InternalClientsRequireSsl $true -ExternalClientsRequireSsl $true -DefaultAuthenticationMethod Ntlm
        Set-ClientAccessService -Identity $server -AutoDiscoverServiceInternalUri "https://autodiscover.$mailDomain/Autodiscover/Autodiscover.xml"
        Set-OrganizationConfig -MapiHttpEnabled $true

        # A 10 GB lab VM crosses the transport's memory back-pressure threshold and stops accepting mail.
        $transportConfig = Join-Path $env:ExchangeInstallPath 'Bin\EdgeTransport.exe.config'
        [xml]$xml = Get-Content -LiteralPath $transportConfig
        $settings = $xml.configuration.appSettings
        $entry = $settings.add | Where-Object key -eq 'EnableResourceMonitoring'
        if (!$entry) { $entry = $xml.CreateElement('add'); $entry.SetAttribute('key', 'EnableResourceMonitoring'); [void]$settings.AppendChild($entry) }
        $entry.SetAttribute('value', 'false')
        $xml.Save($transportConfig)

        $secure = ConvertTo-SecureString $password -AsPlainText -Force
        foreach ($name in $mailboxes) {
            if (!(Get-Mailbox -Identity $name -ErrorAction SilentlyContinue)) {
                $display = (Get-Culture).TextInfo.ToTitleCase($name)
                New-Mailbox -Name $display -Alias $name -UserPrincipalName "$name@$mailDomain" -SamAccountName $name -FirstName $display -Password $secure -ResetPasswordOnNextLogon $false | Out-Null
            }
            Set-ADUser -Identity $name -PasswordNeverExpires $true -UserPrincipalName "$name@$mailDomain"
        }
        Restart-Service MSExchangeTransport, MSExchangeFrontEndTransport, MSExchangeDelivery, MSExchangeSubmission -Force
        iisreset /noforce | Out-Null
    }
    New-Item -ItemType Directory -Force $stateRoot | Out-Null
    $session = New-PSSession -VMName $vmName -Credential $credential
    try { Copy-Item -FromSession $session -Path C:\LabSetup\wino-exchange.cer -Destination $certificatePath -Force } finally { Remove-PSSession $session }
    Write-Step "Server certificate exported to $certificatePath"
}

function Test-MailFlow {
    $credential = Get-DomainCredential
    Write-Step 'Sending a probe between every pair of mailboxes'
    $result = Invoke-Guest -Credential $credential -ArgumentList $mailDomain, $password, $mailboxes -ScriptBlock {
        param($mailDomain, $password, $mailboxes)
        Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn
        $start = Get-Date
        $tag = "wino-probe-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $sent = 0
        foreach ($from in $mailboxes) {
            foreach ($to in $mailboxes | Where-Object { $_ -ne $from }) {
                $credential = [pscredential]::new("$from@$mailDomain", (ConvertTo-SecureString $password -AsPlainText -Force))
                # Port 587 checks authenticated submission, the path a client uses.
                Send-MailMessage -SmtpServer "mail.$mailDomain" -Port 587 -UseSsl -Credential $credential -From "$from@$mailDomain" -To "$to@$mailDomain" -Subject "$tag $from to $to" -Body 'Wino Exchange lab delivery probe' -WarningAction SilentlyContinue
                $sent++
            }
        }
        $deadline = (Get-Date).AddMinutes(4)
        do {
            Start-Sleep -Seconds 10
            $delivered = @(Get-MessageTrackingLog -Start $start -EventId DELIVER -ResultSize Unlimited | Where-Object MessageSubject -like "$tag*").Count
        } while ($delivered -lt $sent -and (Get-Date) -lt $deadline)
        $queues = Get-Queue | Where-Object MessageCount -gt 0 | Select-Object Identity, Status, MessageCount, LastError
        [pscustomobject]@{ Sent = $sent; Delivered = $delivered; Queues = $queues }
    }
    Write-Step "Delivery probe: $($result.Delivered) of $($result.Sent) delivered"
    if ($result.Queues) { $result.Queues | Format-Table -AutoSize | Out-String | Write-Output }
    if ($result.Delivered -lt $result.Sent) { throw 'Mail delivery between lab mailboxes failed. Check Get-Queue and the message tracking log in the VM.' }
}

function Test-HostEndpoints {
    Write-Step "Checking HTTPS endpoints from the host at $vmIp"
    $failures = 0
    $basic = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("alice@${mailDomain}:$password"))
    $checks = @(
        @{ Url = "https://$vmIp/autodiscover/autodiscover.json?Email=alice@$mailDomain&Protocol=EWS"; Expect = 'Exchange.asmx'; Auth = $null },
        @{ Url = "https://autodiscover.$mailDomain/autodiscover/autodiscover.json?Email=alice@$mailDomain&Protocol=EWS"; Expect = 'Exchange.asmx'; Auth = $null },
        @{ Url = "https://mail.$mailDomain/EWS/Exchange.asmx"; Expect = $null; Auth = $basic }
    )
    foreach ($check in $checks) {
        try {
            $headers = @{}
            if ($check.Auth) { $headers.Authorization = $check.Auth }
            $response = Invoke-WebRequest -Uri $check.Url -Headers $headers -UseBasicParsing -TimeoutSec 30
            $ok = !$check.Expect -or $response.Content -match [regex]::Escape($check.Expect)
            Write-Output ("  {0} {1} {2}" -f ($(if ($ok) { 'OK  ' } else { 'FAIL' })), $response.StatusCode, $check.Url)
            if (!$ok) { $failures++ }
        } catch {
            Write-Output "  FAIL $($check.Url): $($_.Exception.Message)"
            $failures++
        }
    }
    if ($failures) { throw "$failures host endpoint check(s) failed. Run 'trust' first if the certificate or names are not set up." }
}

function Set-HostsEntries {
    param([switch]$Remove)
    $hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
    $lines = @(Get-Content -LiteralPath $hostsPath | Where-Object { $_ -notmatch [regex]::Escape($hostsMarker) })
    if (!$Remove) { $lines += "$vmIp $($hostNames -join ' ') $hostsMarker" }
    Set-Content -LiteralPath $hostsPath -Value $lines -Encoding ascii
}

function Install-Trust {
    if (!(Test-Path -LiteralPath $certificatePath)) { throw 'The server certificate is missing. Run exchange-lab.ps1 create first.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    if (!(Get-ChildItem Cert:\CurrentUser\Root | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
        # Windows asks for confirmation. The certificate is a self-signed leaf for the lab names and 192.168.250.10 only.
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
        try { $store.Open('ReadWrite'); $store.Add($certificate) } finally { $store.Dispose() }
    }
    Write-Output "Trusted $($certificate.Subject) ($($certificate.Thumbprint)) for the current user"
}

function Remove-Trust {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
    try {
        $store.Open('ReadWrite')
        foreach ($certificate in @($store.Certificates | Where-Object Subject -eq "CN=mail.$mailDomain")) { $store.Remove($certificate) }
    } finally { $store.Dispose() }
    Write-Output 'Removed the lab certificate from the current user root store'
}

function Show-Status {
    $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
    if (!$vm) { Write-Output "$vmName does not exist. Run create."; return }
    Write-Output "VM: $($vm.State), $([math]::Round($vm.MemoryAssigned / 1GB, 1)) GB assigned, uptime $($vm.Uptime)"
    Write-Output "Checkpoints: $((Get-VMCheckpoint -VMName $vmName | Select-Object -ExpandProperty Name) -join ', ')"
    Write-Output "Ping $vmIp : $(Test-Connection $vmIp -Count 1 -Quiet)"
    if ($vm.State -eq 'Running') {
        Invoke-Guest -Credential (Get-DomainCredential) -ScriptBlock {
            Get-Service MSExchange* | Where-Object StartType -eq 'Automatic' | Where-Object Status -ne 'Running' | ForEach-Object { "Stopped Exchange service: $($_.Name)" }
        }
    }
}

function Invoke-Create {
    foreach ($iso in $serverIso, $exchangeIso) { if (!(Test-Path -LiteralPath $iso)) { throw "Missing $iso" } }
    $phases = 'vm', 'guest', 'forest', 'domain', 'exchange', 'configure', 'mailflow'
    $first = [array]::IndexOf($phases, $StartAt)
    if ($first -eq 0 -and (Get-VM -Name $vmName -ErrorAction SilentlyContinue)) {
        throw "$vmName already exists. Use -StartAt to resume a phase, restore, or remove -Force first."
    }
    $steps = [ordered]@{
        vm        = { Initialize-Network; New-LabVm }
        guest     = { Initialize-Guest }
        forest    = { Install-Forest }
        domain    = { Wait-DomainReady }
        exchange  = { Install-Exchange }
        configure = { Set-ExchangeLab }
        mailflow  = { Test-MailFlow }
    }
    foreach ($phase in $phases[$first..($phases.Count - 1)]) {
        # Install-Forest already waits for the domain.
        if ($phase -eq 'domain' -and $first -lt [array]::IndexOf($phases, 'domain')) { continue }
        Write-Step "Phase: $phase"
        & $steps[$phase]
    }
    Write-Step "Creating checkpoint $checkpointName"
    Stop-VM -Name $vmName
    Checkpoint-VM -Name $vmName -SnapshotName $checkpointName
    Start-VM -Name $vmName
    Write-Step "Done. Next: exchange-lab.ps1 trust, then exchange-lab.ps1 verify"
}

function Invoke-Elevated {
    param([string]$Name, [scriptblock]$Body)
    if (!(Test-Admin)) {
        if ($Elevated) { throw 'Elevation failed.' }
        Invoke-SelfElevated -Name $Name
        return
    }
    if ($Elevated) { Start-Transcript -LiteralPath (Join-Path $stateRoot "$Name.log") -Force | Out-Null }
    try { & $Body } catch { Write-Output "ERROR: $($_.Exception.Message)"; Write-Output $_.ScriptStackTrace; if ($Elevated) { Stop-Transcript | Out-Null; exit 1 } else { throw } }
    if ($Elevated) { Stop-Transcript | Out-Null }
}

switch ($Command) {
    'create' { Invoke-Elevated 'create' { Invoke-Create } }
    'status' { Invoke-Elevated 'status' { Show-Status } }
    'start' { Invoke-Elevated 'start' { Start-VM -Name $vmName } }
    'stop' { Invoke-Elevated 'stop' { Stop-VM -Name $vmName } }
    'checkpoint' { Invoke-Elevated 'checkpoint' { Checkpoint-VM -Name $vmName -SnapshotName "manual-$(Get-Date -Format yyyyMMdd-HHmm)" } }
    'restore' { Invoke-Elevated 'restore' { Restore-VMCheckpoint -VMName $vmName -Name $checkpointName -Confirm:$false; Start-VM -Name $vmName } }
    'verify' {
        Invoke-Elevated 'mailflow' { Test-MailFlow }
        Test-HostEndpoints
    }
    'mailflow' { Invoke-Elevated 'mailflow' { Test-MailFlow } }
    'trust' {
        Install-Trust
        Invoke-Elevated 'hosts' { Set-HostsEntries; Write-Output "Hosts file maps $($hostNames -join ', ') to $vmIp" }
    }
    'hosts' { Invoke-Elevated 'hosts' { Set-HostsEntries; Write-Output "Hosts file maps $($hostNames -join ', ') to $vmIp" } }
    'untrust' {
        Remove-Trust
        Invoke-Elevated 'unhosts' { Set-HostsEntries -Remove; Write-Output 'Removed the lab hosts entries' }
    }
    'unhosts' { Invoke-Elevated 'unhosts' { Set-HostsEntries -Remove; Write-Output 'Removed the lab hosts entries' } }
    'remove' {
        if (!$Force) { throw 'remove deletes the VM, its disks, switch and NAT. Pass -Force.' }
        Invoke-Elevated 'remove' {
            if (Get-VM -Name $vmName -ErrorAction SilentlyContinue) { Stop-VM -Name $vmName -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -Name $vmName -Force }
            Remove-Item -LiteralPath (Join-Path $VmRoot $vmName) -Recurse -Force -ErrorAction SilentlyContinue
            Get-NetNat -Name $natName -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false
            Get-VMSwitch -Name $switchName -ErrorAction SilentlyContinue | Remove-VMSwitch -Force
            Write-Output 'Removed the VM, disk, switch and NAT. ISOs are kept.'
        }
    }
    default { Get-Help $PSCommandPath -Detailed }
}
