$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$meta = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$v = $meta.releases[0].sdk.version
Write-Host "Latest .NET 10 SDK: $v"
$url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$v/dotnet-sdk-$v-win-x64.zip"
Write-Host "URL: $url"
New-Item -ItemType Directory -Force -Path 'D:\software\downloads' | Out-Null
$zip = 'D:\software\downloads\dotnet-sdk-win-x64.zip'
Invoke-WebRequest -Uri $url -OutFile $zip
Write-Host 'Downloaded, extracting...'
Expand-Archive -Path $zip -DestinationPath 'D:\software\dotnet' -Force
& 'D:\software\dotnet\dotnet.exe' --version
