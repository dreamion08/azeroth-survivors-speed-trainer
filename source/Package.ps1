param([string]$OutputDirectory,[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
$dist=Join-Path $repoRoot 'dist'
if(!$OutputDirectory) { $OutputDirectory=Join-Path $dist 'releases' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$metadata=Get-Content -LiteralPath (Join-Path $dist 'build-info.json') -Raw | ConvertFrom-Json
$version=[string]$metadata.version
if($version -notmatch '^\d+\.\d+\.\d+$' -or $metadata.platform -ne 'win-x64' -or $metadata.tests -ne 'passed') { throw 'Build metadata is not a tested Windows release.' }
$binaries=@('Azeroth-Speed.exe','AzerothSpeedHook.dll','Azeroth-Speed-Updater.exe')
foreach($exe in @('Azeroth-Speed.exe','Azeroth-Speed-Updater.exe')) {
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $dist $exe))
    if(('{0}.{1}.{2}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart) -ne $version -or $info.FilePrivatePart -ne 0) { throw "Wrong version for $exe" }
}
$folder=Join-Path $OutputDirectory "Azeroth-Speed-v$version"
$zip=Join-Path $OutputDirectory "Azeroth-Survivors-Speed-Trainer-v$version-win-x64.zip"
if((Test-Path -LiteralPath $folder) -or (Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath "$zip.sha256")) { throw 'Release output already exists. Use a new output directory.' }
New-Item -ItemType Directory -Force -Path (Join-Path $folder 'docs') | Out-Null
foreach($file in $binaries+@('build-info.json')) { Copy-Item -LiteralPath (Join-Path $dist $file) -Destination (Join-Path $folder $file) }
foreach($file in @('README.md','LICENSE','RELEASE_NOTES.md','docs/full-preview.png','docs/mini-preview.png')) { Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination (Join-Path $folder $file) }
$sums=foreach($file in $binaries) { '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $folder $file) -Algorithm SHA256).Hash.ToLowerInvariant(),$file }
[IO.File]::WriteAllText((Join-Path $folder 'SHA256SUMS.txt'),(($sums -join "`n")+"`n"),(New-Object Text.UTF8Encoding($false)))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($folder,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256",("$hash  "+[IO.Path]::GetFileName($zip)+"`n"),(New-Object Text.UTF8Encoding($false)))
Write-Output $folder
Write-Output $zip
Write-Output "$zip.sha256"
