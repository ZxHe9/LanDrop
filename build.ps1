param(
    [switch]$NoPackage,
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$web = Join-Path $root "web"
$assets = Join-Path $root "assets"
$dist = Join-Path $root "dist"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) { throw "找不到 .NET Framework C# 编译器 csc.exe" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$exeName = if ($DebugBuild) { "LanDropDebug.exe" } else { "LanDrop.exe" }
$targetType = if ($DebugBuild) { "exe" } else { "winexe" }
$output = Join-Path $dist $exeName
if (Test-Path $output) { Remove-Item $output -Force }

$sources = Get-ChildItem (Join-Path $src "*.cs") | ForEach-Object { $_.FullName }

$arguments = @(
    "/nologo",
    ("/target:" + $targetType),
    "/platform:anycpu",
    "/optimize+",
    "/langversion:5",
    "/codepage:65001",
    "/warn:4",
    ("/win32manifest:" + (Join-Path $root "app.manifest")),
    ("/win32icon:" + (Join-Path $assets "landrop.ico")),
    ("/resource:" + (Join-Path $web "index.html") + ",Web.index.html"),
    ("/resource:" + (Join-Path $web "app.js") + ",Web.app.js"),
    ("/resource:" + (Join-Path $web "style.css") + ",Web.style.css"),
    ("/resource:" + (Join-Path $web "logo.png") + ",Web.logo.png"),
    ("/resource:" + (Join-Path $assets "landrop.ico") + ",App.icon"),
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Web.Extensions.dll",
    ("/out:" + $output)
)

& $csc @arguments $sources
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
if (-not (Test-Path $output)) { throw "没有生成 $exeName" }

$size = (Get-Item $output).Length
Write-Host ("编译完成: {0} ({1:N0} KB)" -f $output, ($size / 1KB)) -ForegroundColor Green

if (-not $NoPackage) {
    $packageDir = Join-Path $root "release\LanDrop-1.0"
    if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    Copy-Item $output (Join-Path $packageDir "LanDrop.exe")
    foreach ($doc in @("使用说明.txt", "README.md", "LICENSE", "CHANGELOG.md", "SECURITY.md", "GITHUB_UPLOAD.md")) {
        $docPath = Join-Path $root $doc
        if (Test-Path $docPath) { Copy-Item $docPath (Join-Path $packageDir $doc) }
    }
    $docsDir = Join-Path $root "docs"
    if (Test-Path $docsDir) { Copy-Item $docsDir (Join-Path $packageDir "docs") -Recurse }
    $zip = Join-Path $root "release\LanDrop-1.0-Windows.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("已打包: {0} ({1:N0} KB)" -f $zip, ((Get-Item $zip).Length / 1KB)) -ForegroundColor Green
}
