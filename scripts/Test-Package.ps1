$ErrorActionPreference = 'Stop'
dotnet test SubZeroDev.Platform.Updater.slnx -c Release
if ($LASTEXITCODE) { throw 'Library tests failed.' }
dotnet pack src/SubZeroDev.Platform.Updater -c Release -p:ContinuousIntegrationBuild=true -o artifacts/packages
if ($LASTEXITCODE) { throw 'NuGet pack failed.' }
$feed = [IO.Path]::GetFullPath('artifacts/packages')
$cache = [IO.Path]::GetFullPath("artifacts/consumer-cache-$([Guid]::NewGuid().ToString('N').Substring(0, 8))")
$config = [IO.Path]::GetFullPath('artifacts/consumer.nuget.config')
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="local" value="' + [Security.SecurityElement]::Escape($feed) + '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
foreach ($sample in @('WpfTrayHost', 'WinUIHost')) {
    dotnet restore "samples/$sample/$sample.csproj" --configfile $config --packages $cache
    if ($LASTEXITCODE) { throw "$sample clean package restore failed." }
    dotnet build "samples/$sample/$sample.csproj" -c Release --no-restore
    if ($LASTEXITCODE) { throw "$sample build failed." }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Get-ChildItem artifacts/packages/SubZeroDev.Platform.Updater.*.nupkg | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    foreach ($path in @('lib/net10.0/SubZeroDev.Platform.Updater.dll', 'lib/net10.0/SubZeroDev.Platform.Updater.xml', 'README.md', 'SubZeroDev.Platform.Updater.nuspec')) {
        if (-not $zip.GetEntry($path)) { throw "Package missing $path" }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('SubZeroDev.Platform.Updater.nuspec').Open())
    try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($spec.package.metadata.dependencies.group.dependency.id -notcontains 'Velopack') { throw 'Missing transitive bootstrap dependency.' }
    if ($spec.package.metadata.repository.url -ne 'https://github.com/The-Running-Dev/SubZeroDev.Platform.Updater') { throw 'Invalid source repository metadata.' }
} finally { $zip.Dispose() }
if (-not (Get-ChildItem artifacts/packages/*.snupkg)) { throw 'Missing symbols package.' }
