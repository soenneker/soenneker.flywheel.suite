param([int]$Port = 5187)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $projectRoot 'artifacts/publish'
$outputRoot = Join-Path $projectRoot 'out'
dotnet publish (Join-Path $projectRoot 'Soenneker.Flywheel.Website.csproj') -c Release -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Website publish failed.' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Copy-Item -Path (Join-Path $publishRoot 'wwwroot/*') -Destination $outputRoot -Recurse -Force
$serverArgs = @(('"' + (Join-Path $publishRoot 'Soenneker.Flywheel.Website.dll') + '"'), '--urls', "http://127.0.0.1:$Port")
$startOptions = @{FilePath='dotnet'; ArgumentList=$serverArgs; WorkingDirectory=$publishRoot; PassThru=$true; RedirectStandardOutput=(Join-Path $projectRoot 'artifacts/export.log'); RedirectStandardError=(Join-Path $projectRoot 'artifacts/export-error.log')}
if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
$server = Start-Process @startOptions
try {
    $response = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($server.HasExited) { throw 'The static export server exited before rendering.' }
        try { $response = Invoke-WebRequest "http://127.0.0.1:$Port/"; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $response -or $response.StatusCode -ne 200) { throw 'The homepage did not render.' }
    [IO.File]::WriteAllText((Join-Path $outputRoot 'index.html'), $response.Content)
    foreach ($route in @('getting-started', 'dashboard')) {
        $page = Invoke-WebRequest "http://127.0.0.1:$Port/$route"
        if ($page.StatusCode -ne 200) { throw "Page did not render: $route" }
        $pageRoot = Join-Path $outputRoot $route
        New-Item -ItemType Directory -Path $pageRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $pageRoot 'index.html'), $page.Content)
    }
    foreach ($asset in @('css/quark-tailwind.min.css','css/site.css','images/dashboard.png','images/execution.png','favicon.svg','js/code-examples.js','_content/Soenneker.Quark.Suite/js/monacointerop.js','_content/Soenneker.Quark.Suite/js/monaco-editor/monaco.editor.main.esm.js','_content/Soenneker.Quark.Suite/js/monaco-editor/monaco.editor.main.esm.css','_content/Soenneker.Quark.Suite/js/monaco-editor/workers/editor.worker.esm.js')) {
        if (-not (Test-Path (Join-Path $outputRoot $asset))) { throw "Missing public asset: $asset" }
    }
    Write-Output "Static website exported to $outputRoot"
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
