# Builds the HLSL shaders into DXBC and embeds them as resources.
#
# fxc ships with the Windows SDK.  We compile ahead of time so the app
# never needs a runtime HLSL compiler (D3DCompile is not available on
# the D3D11 feature level we target on all machines, and linking a
# compiler in for two tiny shaders is not worth it).
#
# Two output files, because fxc emits one binary blob per entry point:
#
#   Panel.vertex.cso   - the vertex shader
#   Panel.pixel.cso    - the pixel shader
#
# Both are added to the project as EmbeddedResource with a logical name,
# so the C# side can load them with Assembly.GetManifestResourceStream.
#
# The file names matter more than they look: MSBuild decides whether an
# EmbeddedResource is a satellite assembly from the file's extension.  An
# extension of "ps" is a valid culture code (Pashto), so a file called
# Panel.ps.cso gets quietly moved out of the main assembly into a
# satellite DLL and disappears from GetManifestResourceNames().  "vertex"
# and "pixel" are not culture codes, so they stay put.

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$shaderDir = Join-Path $projectDir "Spatial\Shaders"
$outDir = Join-Path $projectDir "obj\Shaders\$Configuration"

# Locate fxc.exe: prefer the newest Windows SDK that has it.
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$fxc = $null
if (Test-Path $sdkRoot) {
    # The path looks like  ...\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe
    # so the version is the directory two levels up from the executable.
    $candidates = Get-ChildItem $sdkRoot -Recurse -Filter "fxc.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Sort-Object { $_.Directory.Parent.Name } -Descending
    if ($candidates) { $fxc = $candidates[0].FullName }
}

if (-not $fxc) {
    # Not fatal: the app falls back to a solid-colour panel.  Log loudly
    # so the developer knows why.
    Write-Warning "fxc.exe not found - shaders will not be compiled. Install the Windows SDK."
    exit 0
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$src = Join-Path $shaderDir "Panel.hlsl"

Write-Host "Using $fxc"

# /T vs_4_0_level_9_1 is the lowest profile that every D3D11 device
# supports.  It is enough for a textured quad with a constant buffer.
# /E names the entry point.  /Fh names the output file.
& $fxc /nologo /T vs_4_0_level_9_1 /E VSMain /Fo (Join-Path $outDir "Panel.vertex.cso") $src
if ($LASTEXITCODE -ne 0) { throw "fxc failed for the vertex shader" }

& $fxc /nologo /T ps_4_0_level_9_1 /E PSMain /Fo (Join-Path $outDir "Panel.pixel.cso") $src
if ($LASTEXITCODE -ne 0) { throw "fxc failed for the pixel shader" }

Write-Host "Shaders compiled into $outDir"
