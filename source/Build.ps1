param([string]$GameImage)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outRoot=Join-Path $repoRoot 'dist'
$buildRoot=Join-Path $repoRoot 'build'
New-Item -ItemType Directory -Force -Path $outRoot,$buildRoot | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vsRoot) { throw 'Visual Studio C++ x64 Build Tools are required.' }
$vcRoot=(Get-ChildItem -LiteralPath (Join-Path $vsRoot 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVer=(Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$env:PATH="$vcRoot\bin\Hostx64\x64;$env:PATH"
$env:INCLUDE="$vcRoot\include;$sdkRoot\Include\$sdkVer\ucrt;$sdkRoot\Include\$sdkVer\shared;$sdkRoot\Include\$sdkVer\um"
$env:LIB="$vcRoot\lib\x64;$sdkRoot\Lib\$sdkVer\ucrt\x64;$sdkRoot\Lib\$sdkVer\um\x64"
$cl=Join-Path $vcRoot 'bin\Hostx64\x64\cl.exe'
$nativeSource=Join-Path $PSScriptRoot 'SpeedHook.cpp'
$dll=Join-Path $outRoot 'AzerothSpeedHook.dll'
& $cl /nologo /O2 /W4 /EHsc /MT /std:c++17 /utf-8 /guard:cf /LD $nativeSource "/Fo:$(Join-Path $buildRoot 'SpeedHook.obj')" /link "/OUT:$dll" "/IMPLIB:$(Join-Path $buildRoot 'SpeedHook.lib')" /DYNAMICBASE /NXCOMPAT /GUARD:CF
if($LASTEXITCODE -ne 0) { throw 'Native module build failed.' }
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$manifest=Join-Path $PSScriptRoot 'app.manifest'
$compatibility=Join-Path $PSScriptRoot 'TargetCompatibility.cs'
$wheel=Join-Path $PSScriptRoot 'WheelInput.cs'
$target=Join-Path $PSScriptRoot 'GameTarget.cs'
$assembly=Join-Path $PSScriptRoot 'AssemblyInfo.cs'
$updateClient=Join-Path $PSScriptRoot 'UpdateClient.cs'
$updateInstaller=Join-Path $PSScriptRoot 'UpdateInstaller.cs'
$updateReferences=@('/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$managed=@((Join-Path $PSScriptRoot 'Trainer.cs'),$compatibility,$wheel,$target,$assembly,(Join-Path $PSScriptRoot 'AppUpdates.cs'),$updateClient,$updateInstaller)
& $csc /nologo /target:winexe /platform:x64 /optimize+ /main:Program "/win32manifest:$manifest" "/out:$(Join-Path $outRoot 'Azeroth-Speed.exe')" /r:System.Windows.Forms.dll /r:System.Drawing.dll @updateReferences @managed
if($LASTEXITCODE -ne 0) { throw 'Trainer build failed.' }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /main:UpdaterProgram "/win32manifest:$manifest" "/out:$(Join-Path $outRoot 'Azeroth-Speed-Updater.exe')" /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $assembly $updateInstaller
if($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
$updateClientTest=Join-Path $buildRoot 'UpdateClientTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:UpdateClientTests "/out:$updateClientTest" @updateReferences $assembly $updateClient (Join-Path $PSScriptRoot 'UpdateClientTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Update client test build failed.' }
& $updateClientTest
if($LASTEXITCODE -ne 0) { throw 'Update client tests failed.' }
$updateInstallerTest=Join-Path $buildRoot 'UpdateInstallerTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:UpdateInstallerTests "/out:$updateInstallerTest" /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $assembly $updateInstaller (Join-Path $PSScriptRoot 'UpdateInstallerTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Update installer test build failed.' }
& $updateInstallerTest
if($LASTEXITCODE -ne 0) { throw 'Update installer tests failed.' }
$updateFlowTest=Join-Path $buildRoot 'UpdateFlowTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:UpdateFlowTests "/out:$updateFlowTest" /r:System.Windows.Forms.dll @updateReferences $assembly (Join-Path $PSScriptRoot 'AppUpdates.cs') $updateClient $updateInstaller (Join-Path $PSScriptRoot 'UpdateFlowTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Update flow test build failed.' }
& $updateFlowTest (Join-Path $outRoot 'Azeroth-Speed-Updater.exe') $outRoot
if($LASTEXITCODE -ne 0) { throw 'Production updater flow tests failed.' }
$nativeTest=Join-Path $buildRoot 'SpeedHookTests.exe'
& $cl /nologo /O2 /W4 /EHsc /MT /std:c++17 /utf-8 /guard:cf /DAZS_SELF_TEST $nativeSource "/Fo:$(Join-Path $buildRoot 'SpeedHookTests.obj')" "/Fe:$nativeTest" /link /DYNAMICBASE /NXCOMPAT /GUARD:CF
if($LASTEXITCODE -ne 0) { throw 'Native test build failed.' }
& $nativeTest
if($LASTEXITCODE -ne 0) { throw 'Native timing/path tests failed.' }
$uiTest=Join-Path $buildRoot 'MiniUiTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /main:MiniUiTests "/win32manifest:$manifest" "/out:$uiTest" /r:System.Windows.Forms.dll /r:System.Drawing.dll @updateReferences @managed (Join-Path $PSScriptRoot 'MiniUiTests.cs')
if($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
& $uiTest (Join-Path $buildRoot 'previews')
if($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
$compatibilityTest=Join-Path $buildRoot 'CompatibilityTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ "/out:$compatibilityTest" $compatibility (Join-Path $PSScriptRoot 'CompatibilityTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Compatibility test build failed.' }
if($GameImage) { & $compatibilityTest $GameImage } else { & $compatibilityTest }
if($LASTEXITCODE -ne 0) { throw 'Compatibility tests failed.' }
$targetTest=Join-Path $buildRoot 'GameTargetTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ "/out:$targetTest" $target $compatibility (Join-Path $PSScriptRoot 'GameTargetTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Target test build failed.' }
& $targetTest
if($LASTEXITCODE -ne 0) { throw 'Target tests failed.' }
$wheelTest=Join-Path $buildRoot 'WheelInputTests.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ "/out:$wheelTest" /r:System.Windows.Forms.dll /r:System.Drawing.dll $wheel (Join-Path $PSScriptRoot 'WheelInputTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Wheel test build failed.' }
& $wheelTest
if($LASTEXITCODE -ne 0) { throw 'Wheel tests failed.' }
$portableTestFolder=Join-Path $buildRoot 'portable test Åzeroth'
New-Item -ItemType Directory -Force -Path $portableTestFolder | Out-Null
$portableTest=Join-Path $portableTestFolder 'AzerothSurvivors.exe'
& $cl /nologo /O2 /W4 /EHsc /MT /std:c++17 /utf-8 /guard:cf (Join-Path $PSScriptRoot 'PortableIntegrationTests.cpp') "/Fo:$(Join-Path $buildRoot 'PortableIntegrationTests.obj')" "/Fe:$portableTest" /link /DYNAMICBASE /NXCOMPAT /GUARD:CF
if($LASTEXITCODE -ne 0) { throw 'Portable integration host build failed.' }
& $portableTest $dll
if($LASTEXITCODE -ne 0) { throw 'Production module integration failed.' }
& $portableTest $dll --mismatch
if($LASTEXITCODE -ne 0) { throw 'Production module target-rejection test failed.' }
$versionInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outRoot 'Azeroth-Speed.exe'))
$releaseVersion='{0}.{1}.{2}' -f $versionInfo.FileMajorPart,$versionInfo.FileMinorPart,$versionInfo.FileBuildPart
$metadata=[ordered]@{version=$releaseVersion;platform='win-x64';protocol=3;tests='passed'} | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $outRoot 'build-info.json'),$metadata,(New-Object Text.UTF8Encoding($false)))
Write-Output "Version $releaseVersion build and all checks passed."
