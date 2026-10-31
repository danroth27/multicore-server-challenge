param(
    [Parameter(Mandatory)][string] $Name,
    [string] $Source = (Split-Path $PSScriptRoot -Parent),
    [string] $Reuse = '',
    [hashtable] $Environment = @{},
    [hashtable] $Properties = @{},
    [string] $Description = '',
    [string] $Root = (Join-Path $PSScriptRoot 'work')
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path $Source).Path
$Root = [IO.Path]::GetFullPath($Root)
$directory = Join-Path $Root "candidates\$Name"
if (Test-Path (Join-Path $directory 'candidate.json')) { throw "Candidate '$Name' already exists; choose a new name." }
New-Item -ItemType Directory -Force $directory | Out-Null
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if ($Reuse) {
        if ($Properties.Count) { throw 'Build-property changes require a fresh publish, not -Reuse.' }
        $parent = Get-Content (Join-Path $Root "candidates\$Reuse\candidate.json") -Raw | ConvertFrom-Json
        $app = $parent.app
        $snapshot = $parent.snapshot
        $sdk = $parent.sdk
        $sha = $parent.sha256
        $Properties = @{}
        foreach ($property in $parent.properties.PSObject.Properties) {
            $Properties[$property.Name] = $property.Value
        }
    }
    else {
        $app = Join-Path $directory 'app'
        $arguments = @('publish', (Join-Path $Source 'Conduit.csproj'), '-c', 'Release', '-o', $app,
            '-p:UseAppHost=false', '--nologo', '-v:q')
        foreach ($key in $Properties.Keys) { $arguments += "-p:$key=$($Properties[$key])" }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Name" }
        $snapshot = Join-Path $directory 'source.zip'
        $files = Get-ChildItem -LiteralPath $Source -Force |
            Where-Object Name -NotIn @('bin', 'obj', 'benchmarks', 'tests')
        Compress-Archive -LiteralPath $files.FullName -DestinationPath $snapshot -Force
        $sdk = & dotnet --version
        $sha = (Get-FileHash (Join-Path $app 'conduit.dll') -Algorithm SHA256).Hash
    }
    [ordered]@{
        name = $Name; app = $app; snapshot = $snapshot; sdk = $sdk; sha256 = $sha
        environment = $Environment; properties = $Properties; description = $Description
        created_utc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory 'candidate.json')
}
finally { Pop-Location }
