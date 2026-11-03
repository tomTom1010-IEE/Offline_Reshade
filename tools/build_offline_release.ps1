param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$BaselineArchive,
    [switch]$SkipBuild,
    [switch]$RunIntegrationTests,
    [switch]$RequireCleanSource
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseline = Get-Content -LiteralPath (Join-Path $repo 'maintenance\baseline.json') -Raw | ConvertFrom-Json
if (!$Version) { $Version = $baseline.productVersion }
if ($Version -ne $baseline.productVersion) { throw 'Version must match maintenance/baseline.json; update the baseline and generated contract together.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "offline-v$Version") { throw 'Release tag does not match the baseline version.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'build\offline-release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$checks = [Collections.Generic.List[object]]::new()
$reportPath = Join-Path $OutputDirectory "verification-$stamp.json"
$passed = $false
Start-Transcript -Path (Join-Path $OutputDirectory "build-$stamp.log") | Out-Null
$stage = Join-Path $OutputDirectory "OfflineReShade-$Version-win-x64-$stamp"
# Fresh staging avoids carrying stale user settings/resources into a release.
New-Item -ItemType Directory -Path $stage | Out-Null
$env:NUGET_PACKAGES = Join-Path $repo 'build\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo 'build\nuget\http'
$savedBuildPath = $env:PATH
Remove-Item Env:Path
$env:PATH = $savedBuildPath
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio 2022 C++ build tools are required by the maintenance baseline.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    if ($Program -eq $msbuild) { $Arguments = @($Arguments | Where-Object { $_ -ne '/m' }) + @('/m:1', '/nr:false', "/p:SolutionDir=$repo\") }
    if ($Program -eq $msbuild -and $Arguments[0].EndsWith('.vcxproj')) {
        $Arguments += @("/p:WindowsTargetPlatformVersion=$($baseline.toolchain.windowsSdk)", "/p:PlatformToolset=$($baseline.toolchain.platformToolset)")
        # MSVC 14.44 produced intermittent C1001 failures during whole-program codegen.
        # Keep regular Release optimization, but rebuild all objects without /GL.
        $Arguments += "/p:WholeProgramOptimization=$($baseline.toolchain.wholeProgramOptimization.ToString().ToLowerInvariant())"
    }
    $started = [DateTime]::UtcNow
    & $Program @Arguments
    $checks.Add(@{ program = [IO.Path]::GetFileName($Program); arguments = $Arguments; exitCode = $LASTEXITCODE; durationSeconds = ([DateTime]::UtcNow - $started).TotalSeconds })
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
Push-Location $repo
try {
    $pythonVersion = (& python -c 'import platform; print(platform.python_version())').Trim()
    if ($LASTEXITCODE -ne 0 -or $pythonVersion -ne $baseline.toolchain.python) { throw "Python $($baseline.toolchain.python) x64 is required; found $pythonVersion" }
    $pythonEnvironment = Join-Path $repo "build\python-$pythonVersion"
    if (!(Test-Path -LiteralPath (Join-Path $pythonEnvironment 'Scripts\python.exe'))) { Invoke-Checked 'python' @('-m', 'venv', $pythonEnvironment) }
    $env:PATH = (Join-Path $pythonEnvironment 'Scripts') + [IO.Path]::PathSeparator + $env:PATH
    Invoke-Checked 'python' @('-m', 'pip', 'install', '--require-hashes', '--only-binary=:all:', '-r', 'tests\requirements.lock.txt')
    Invoke-Checked 'python' @('tools\verify_offline_baseline.py', '--provenance', (Join-Path $stage 'source-provenance.json'))
    $provenance = Get-Content -LiteralPath (Join-Path $stage 'source-provenance.json') -Raw | ConvertFrom-Json
    if ($RequireCleanSource -and $provenance.sourceDirty) { throw 'Release CI requires a clean source tree, including untracked build inputs.' }
    $sdk = (& dotnet --version).Trim()
    if ($sdk -ne $baseline.toolchain.dotnetSdk) { throw "Unexpected .NET SDK: $sdk" }
    $actualToolchain = @{ dotnetSdk = $sdk; msbuild = (& $msbuild -version -nologo | Select-Object -Last 1).Trim(); vcTools = (Get-Content -LiteralPath (Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim(); windowsSdk = $baseline.toolchain.windowsSdk; wholeProgramOptimization = $baseline.toolchain.wholeProgramOptimization; runnerImage = $env:ImageVersion; nativeBuildSkipped = [bool]$SkipBuild }
    if (!$SkipBuild) {
        Invoke-Checked $msbuild @('OfflineReShadeWinUI\OfflineReShadeWinUI.csproj', '/restore', '/t:Publish', '/p:Configuration=Release', '/p:Platform=x64', '/p:SelfContained=true', "/p:Version=$Version", "/p:PublishDir=$stage\", '/p:DebugType=None', '/p:DebugSymbols=false', '/m', '/v:minimal', '/nologo')
        foreach ($project in @('ReShade.vcxproj', 'OfflineReShadePrototype.vcxproj', 'OfflineReShadePreviewBridge.vcxproj')) {
            Invoke-Checked $msbuild @($project, '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/v:minimal', '/nologo')
        }
    } else {
        # SkipBuild only skips C++ recompilation; publish still generates an isolated .NET layout.
        Invoke-Checked $msbuild @('OfflineReShadeWinUI\OfflineReShadeWinUI.csproj', '/restore', '/t:Publish', '/p:Configuration=Release', '/p:Platform=x64', "/p:PublishDir=$stage\", '/p:DebugType=None', '/p:DebugSymbols=false', '/v:minimal', '/nologo')
    }
    Invoke-Checked 'dotnet' @('publish', 'OfflineReShade.Mcp\OfflineReShade.Mcp.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $stage 'Mcp'), "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false', '--nologo', '-v', 'minimal')
    foreach ($name in @('OfflineReShadePrototype.exe', 'OfflineReShadePreviewBridge.dll', 'ReShade64.dll')) {
        Copy-Item -LiteralPath (Join-Path $repo "bin\x64\Release\$name") -Destination $stage
    }
    foreach ($name in @('README.md', 'README.zh-CN.md', 'LICENSE.md', 'CHANGELOG.md', 'offlineReshadelogo.png')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $stage }
    New-Item -ItemType Directory -Path (Join-Path $stage 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'docs\MCP.md') -Destination (Join-Path $stage 'docs')
    Copy-Item -LiteralPath (Join-Path $repo 'docs\ADDON_SERVICES.md') -Destination (Join-Path $stage 'docs')
    Copy-Item -LiteralPath (Join-Path $repo 'docs\STABILITY_PLAN.md') -Destination (Join-Path $stage 'docs')
    Copy-Item -LiteralPath (Join-Path $repo 'docs\MAINTENANCE.md') -Destination (Join-Path $stage 'docs')
    New-Item -ItemType Directory -Path (Join-Path $stage 'maintenance') | Out-Null
    foreach ($name in @('baseline.json', 'interfaces.lock.json')) { Copy-Item -LiteralPath (Join-Path $repo "maintenance\$name") -Destination (Join-Path $stage 'maintenance') }
    foreach ($name in @('Effects', 'Textures', 'Addons')) {
        $dir = Join-Path $stage $name; New-Item -ItemType Directory -Path $dir | Out-Null
        "Install your own ReShade $name resources here." | Set-Content -LiteralPath (Join-Path $dir 'README.txt') -Encoding utf8
    }
    $config = @{ mcpServers = @{ offline_reshade = @{ command = (Join-Path $stage 'Mcp\OfflineReShade.Mcp.exe'); args = @('--native', (Join-Path $stage 'OfflineReShadePrototype.exe')) } } }
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'mcp-config.example.json') -Encoding utf8
    # NuGet license metadata is reproducible from the resolved dependency graph.
    $notices = @('Offline ReShade third-party dependencies', 'ReShade: BSD-3-Clause; see LICENSE.md', '')
    foreach ($assets in @('OfflineReShadeWinUI\obj\project.assets.json', 'OfflineReShade.Mcp\obj\project.assets.json')) {
        $graph = Get-Content -LiteralPath (Join-Path $repo $assets) -Raw | ConvertFrom-Json
        foreach ($entry in $graph.libraries.PSObject.Properties) {
            if ($entry.Value.type -ne 'package') { continue }
            $nuspec = Join-Path $env:NUGET_PACKAGES ($entry.Name.ToLowerInvariant() + '/' + $entry.Name.Split('/')[0].ToLowerInvariant() + '.nuspec')
            if (Test-Path -LiteralPath $nuspec) {
                [xml]$meta = Get-Content -LiteralPath $nuspec -Raw
                $notices += "$($entry.Name): $($meta.package.metadata.license.InnerText) $($meta.package.metadata.licenseUrl)"
            }
        }
    }
    $notices | Select-Object -Unique | Set-Content -LiteralPath (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Encoding utf8
    foreach ($required in @('OfflineReShadeWinUI.exe', 'App.xbf', 'OfflineReShadeWinUI.pri', 'Views\MainWindow.xbf', 'coreclr.dll', 'hostfxr.dll', 'Mcp\OfflineReShade.Mcp.exe', 'Mcp\coreclr.dll', 'ReShade64.dll', 'OfflineReShadePrototype.exe', 'OfflineReShadePreviewBridge.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Missing runtime file: $required" }
    }
    $forbidden = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -match '(?i)\.(fx|fxh|addon64|pdb|lib|exp|log)$|settings.*\.json$' }
    if ($forbidden) { throw "Unexpected file in package: $($forbidden[0].FullName)" }
    if ($RunIntegrationTests) {
        Invoke-Checked $msbuild @('tests\OfflineReShade.AddonService.Tests.vcxproj', '/p:Configuration=Release', '/p:Platform=x64', '/v:minimal', '/nologo')
        Invoke-Checked 'python' @('tests\run_addon_services.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "addon-services-$stamp"), '--addon', (Join-Path $repo 'build\addon-service-fixture\ServiceFixture.addon64'))
        Invoke-Checked 'python' @('tools\check_workflows.py')
        Invoke-Checked 'python' @('-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_maintenance.py', '-v')
        Invoke-Checked $msbuild @('tests\OfflineReShade.Graphics.Tests.vcxproj', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/v:minimal', '/nologo')
        Invoke-Checked (Join-Path $repo 'bin\x64\Release\OfflineReShade.Graphics.Tests.exe') @()
        Invoke-Checked $msbuild @('tests\OfflineReShade.CaptureContract.vcxproj', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/v:minimal', '/nologo')
        Invoke-Checked 'dotnet' @('run', '--project', 'tests\OfflineReShade.Core.Tests\OfflineReShade.Core.Tests.csproj', '-c', 'Release', '--nologo')
        Invoke-Checked 'python' @('tests\run_integration.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "verification-$stamp"))
        Invoke-Checked 'python' @('tests\run_input_workflow.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "input-workflow-$stamp"))
        Invoke-Checked 'python' @('tests\run_export_workflow.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "export-workflow-$stamp"))
        Invoke-Checked 'python' @('tests\run_edit_history.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "edit-history-$stamp"))
        Invoke-Checked 'python' @('tests\run_batch_workflow.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "batch-workflow-$stamp"))
        Invoke-Checked 'python' @('tests\run_preview_workflow.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "preview-workflow-$stamp"), '--graphics-sampler', (Join-Path $repo 'bin\x64\Release\OfflineReShade.Graphics.Tests.exe'))
        Invoke-Checked 'dotnet' @('run', '--project', 'tests\OfflineReShade.Core.Tests\OfflineReShade.Core.Tests.csproj', '-c', 'Release', '--no-build', '--', '--native-lifecycle', (Join-Path $stage 'OfflineReShadePrototype.exe'), (Join-Path $OutputDirectory "verification-$stamp"))
        Invoke-Checked 'python' @('tests\run_output_correctness.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "correctness-$stamp"))
        Invoke-Checked 'python' @('tests\run_d3d_recovery.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "graphics-$stamp"))
        Invoke-Checked 'python' @('tests\run_compatibility.py', '--package', $stage, '--work', (Join-Path $OutputDirectory "compatibility-$stamp"))
    }
    Invoke-Checked 'python' @('tools\verify_offline_baseline.py', '--provenance', (Join-Path $OutputDirectory "source-after-$stamp.json"))
    $sourceAfter = Get-Content -LiteralPath (Join-Path $OutputDirectory "source-after-$stamp.json") -Raw | ConvertFrom-Json
    if ($sourceAfter.sourceSnapshotSha256 -ne $provenance.sourceSnapshotSha256) { throw 'Source changed during the build; rerun against a stable working tree.' }
    $verification = @{ status = 'passed'; integrationTests = [bool]$RunIntegrationTests; nativeBuildSkipped = [bool]$SkipBuild; commands = $checks.ToArray() }
    $verification | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'verification.json') -Encoding utf8
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        @{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{ schemaVersion = 2; version = $Version; provenance = $provenance; toolchainActual = $actualToolchain; builtAtUtc = (Get-Date).ToUniversalTime().ToString('o'); files = $files } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8
    $zip = "$stage.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    if ($BaselineArchive) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $baseline = Join-Path $OutputDirectory "baseline-$stamp"; [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($BaselineArchive), $baseline)
        $patch = Join-Path $OutputDirectory "OfflineReShade-$Version-update-$stamp"; New-Item -ItemType Directory -Path $patch | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
            $relative = $file.FullName.Substring($stage.Length + 1); $old = Join-Path $baseline $relative
            if (!(Test-Path -LiteralPath $old) -or (Get-FileHash -LiteralPath $old).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                $dest = Join-Path $patch $relative; New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null; Copy-Item -LiteralPath $file.FullName -Destination $dest
            }
        }
        'Close WinUI and MCP, then extract over the existing installation. User settings and shader resources are preserved.' | Set-Content -LiteralPath (Join-Path $patch 'UPDATE.txt')
        Compress-Archive -Path (Join-Path $patch '*') -DestinationPath "$patch.zip" -CompressionLevel Optimal
        Get-FileHash -LiteralPath "$patch.zip" -Algorithm SHA256
    }
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
    Invoke-Checked 'python' @('tools\verify_offline_package.py', $zip)
    $passed = $true
    Write-Output "PACKAGE_DIRECTORY=$stage"
    Write-Output "PACKAGE_ZIP=$zip"
    Write-Output "SHA256=$($hash.Hash)"
} finally {
    @{ status = $(if ($passed) { 'passed' } else { 'failed' }); integrationTests = [bool]$RunIntegrationTests; commands = $checks.ToArray() } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Pop-Location
    $env:PATH = $savedBuildPath
    Stop-Transcript | Out-Null
}
