<#
.SYNOPSIS
Script for installing and launching cosmos emulator

.DESCRIPTION
This script downloads, installs and launches cosmosdb-emulator.

.PARAMETER EmulatorMsiUrl
Uri for downloading the cosmosdb-emulator

.PARAMETER StartParameters
Parameter with which to launch the cosmosdb-emulator\

.PARAMETER Emulator
Exact path to Microsoft.Azure.Cosmos.Emulator.exe

.PARAMETER Stage
Determines what part of the script to run. Has to be either Install or Launch
#>
[CmdletBinding()]
Param (
  [string] $EmulatorMsiUrl = "https://aka.ms/cosmosdb-emulator",
  [string] $StartParameters,
  [string] $Emulator,
  [Parameter(Mandatory=$True)]
  [ValidateSet('Install', 'Launch')]
  [string] $Stage
)

$targetDir = Join-Path $Env:Temp AzureCosmosEmulator
$logFile = Join-Path $Env:Temp log.txt
$productName = "Azure Cosmos DB Emulator"

if ([string]::IsNullOrEmpty($Emulator))
{
  $Emulator = (Join-Path $targetDir (Join-Path $productName "Microsoft.Azure.Cosmos.Emulator.exe"))
}

if ($Stage -eq "Install")
{
  $downloadTryCount = 0
  New-Item $targetDir -Type Directory
  New-Item $logFile -Type File
  do
  {
    # Download and Extract Public Cosmos DB Emulator
    Write-Host "Downloading and extracting Cosmos DB Emulator - $EmulatorMsiUrl"
    Write-Host "Target Directory $targetDir"
    Write-Host "Log File $logFile"

    $downloadTryCount++
    Write-Host "Download Try Count: $downloadTryCount"
    Remove-Item -Path (Join-Path $targetDir '*') -Recurse
    Clear-Content -Path $logFile

    Add-MpPreference -ExclusionPath $targetDir

    $installProcess  = Start-Process msiexec -Wait -PassThru -ArgumentList "/a $EmulatorMsiUrl TARGETDIR=$targetDir /qn /liew $logFile"
    Get-Content $logFile
    Write-Host "Exit Code: $($installProcess.ExitCode)"
  }
  while(($installProcess.ExitCode -ne 0) -and ($downloadTryCount -lt 3))

  if(Test-Path (Join-Path $Env:LOCALAPPDATA CosmosDbEmulator))
  {
    Write-Host "Deleting Cosmos DB Emulator data"
    Remove-Item -Recurse -Force $Env:LOCALAPPDATA\CosmosDbEmulator
  }

  Write-Host "Getting Cosmos DB Emulator Version"
  $fileVersion = Get-ChildItem $Emulator
  Write-Host $Emulator $fileVersion.VersionInfo
}

if ($Stage -eq "Launch")
{
  Write-Host "Launching Cosmos DB Emulator"
  if (!(Test-Path $Emulator)) {
    Write-Error "The emulator is not installed where expected at '$Emulator'"
    return
  }

  $process = Start-Process $Emulator -ArgumentList "/getstatus" -PassThru -Wait
  switch ($process.ExitCode) {
    1 {
      Write-Host "The emulator is already starting"
      return
    }
    2 {
      Write-Host "The emulator is already running"
      return
    }
    3 {
      Write-Host "The emulator is stopped"
    }
    default {
      Write-Host "Unrecognized exit code $($process.ExitCode)"
      return
    }
  }

  $argumentList = ""
  if (-not [string]::IsNullOrEmpty($StartParameters)) {
      $argumentList += , $StartParameters
  } else {
    # Use the default params if none provided
    $argumentList = "/noexplorer /noui /enablepreview /EnableSqlComputeEndpoint /disableratelimiting /partitioncount=10 /consistency=Strong"
  }

  Write-Host "Starting emulator process: $Emulator $argumentList"
  $process = Start-Process $Emulator -ArgumentList $argumentList -ErrorAction Stop -PassThru
  Write-Host "Emulator process started: $($process.Name), $($process.FileVersion)"

  $Timeout = 600
  $result="NotYetStarted"
  $complete = if ($Timeout -gt 0) {
    $start = [DateTimeOffset]::Now
    $stop = $start.AddSeconds($Timeout)
    {
      $result -eq "Running" -or [DateTimeOffset]::Now -ge $stop
    }
  }
  else {
    {
      $result -eq "Running"
    }
  }

  do {
    $process = Start-Process $Emulator -ArgumentList "/getstatus" -PassThru -Wait
    switch ($process.ExitCode) {
      1 {
        Write-Host "The emulator is starting"
      }
      2 {
        Write-Host "The emulator is running"
        $result="Running"
        return
      }
      3 {
        Write-Host "The emulator is stopped"
      }
      default {
        Write-Host "Unrecognized exit code $($process.ExitCode)"
      }
    }
    Start-Sleep -Seconds 5
  }
  until ($complete.Invoke())
  Write-Error "The emulator failed to reach Running status within ${Timeout} seconds"
}