param([ValidateSet('File','Uri')][string]$Kind,[string]$InputValue)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.System.Launcher,Windows.System,ContentType=WindowsRuntime]
$null=[Windows.System.LauncherOptions,Windows.System,ContentType=WindowsRuntime]
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
function Await($operation,[Type]$type) {
 $method=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'} | Select-Object -First 1
 $task=$method.MakeGenericMethod($type).Invoke($null,@($operation))
 if(-not $task.Wait(20000)){throw 'Activation timed out after 20 seconds.'}
 $task.Result
}
$options=New-Object Windows.System.LauncherOptions
$options.TargetApplicationPackageFamilyName='58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj'
if($Kind -eq 'File'){
 $file=Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($InputValue)) ([Windows.Storage.StorageFile])
 $result=Await ([Windows.System.Launcher]::LaunchFileAsync($file,$options)) ([bool])
}else{
 $result=Await ([Windows.System.Launcher]::LaunchUriAsync([Uri]$InputValue,$options)) ([bool])
}
[pscustomobject]@{kind=$Kind;input=$InputValue;accepted=$result;time=(Get-Date -Format o)} | ConvertTo-Json
Start-Sleep -Seconds 5
