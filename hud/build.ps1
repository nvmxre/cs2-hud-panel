# Compiles the example layout and stylesheet into a CS2 addon.
#
# The panel's structure is a Panorama resource, so it has to reach the client before anything
# renders. This script copies the sources into an addon's content folder and compiles them to
# .vxml_c / .vcss_c. Publishing that addon to the Workshop and delivering it with MultiAddonManager
# is the other half — see the README.
#
# Usage:
#   powershell -File hud/build.ps1
# The CS2 install is found through Steam's library folders; override it with -Cs2 "<path>".
#
# Workshop Tools are required and are NOT in Steam's tools list. Install them from inside the game:
#   CS2 -> Settings -> search "Install Counter-Strike Workshop Tools" -> Yes -> quit the game.
#   You will know they arrived when a content\ folder appears next to game\.

param(
    [string]$Cs2 = "",
    [string]$Addon = "my_hud",
    [string]$Name = "example_menu"
)

$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Cs2) {
    $roots = @("C:\Program Files (x86)\Steam")
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        $found = Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches
        foreach ($line in $found) {
            foreach ($m in $line.Matches) { $roots += $m.Groups[1].Value.Replace('\\', '\') }
        }
    }
    foreach ($r in $roots) {
        $candidate = Join-Path $r "steamapps\common\Counter-Strike Global Offensive"
        if (Test-Path $candidate) { $Cs2 = $candidate; break }
    }
}

if (-not $Cs2 -or -not (Test-Path $Cs2)) {
    Write-Output "Could not find Counter-Strike 2. Pass the path with -Cs2"
    exit 1
}
Write-Output "Game: $Cs2"

$compiler = Join-Path $Cs2 "game\bin\win64\resourcecompiler.exe"
if (-not (Test-Path $compiler)) {
    Write-Output ""
    Write-Output "Workshop Tools are not installed: resourcecompiler.exe is missing."
    Write-Output "They are not in Steam's tools list. Install them from inside the game:"
    Write-Output "  1. Launch CS2"
    Write-Output "  2. Settings -> search 'Install Counter-Strike Workshop Tools' -> Yes"
    Write-Output "  3. Quit the game and let Steam download them"
    Write-Output "Ready when a content\ folder appears next to game\"
    exit 1
}

$layoutDir = Join-Path $Cs2 "content\csgo_addons\$Addon\panorama\layout\custom_game"
$styleDir = Join-Path $Cs2 "content\csgo_addons\$Addon\panorama\styles\custom_game"
New-Item -ItemType Directory -Force -Path $layoutDir, $styleDir | Out-Null

Copy-Item (Join-Path $src "layout\$Name.xml") $layoutDir -Force
Copy-Item (Join-Path $src "styles\$Name.css") $styleDir -Force
Write-Output "Sources copied into content\csgo_addons\$Addon"

& $compiler -i (Join-Path $layoutDir "$Name.xml") -r
& $compiler -i (Join-Path $styleDir "$Name.css") -r

$outLayout = Join-Path $Cs2 "game\csgo_addons\$Addon\panorama\layout\custom_game\$Name.vxml_c"
$outStyle = Join-Path $Cs2 "game\csgo_addons\$Addon\panorama\styles\custom_game\$Name.vcss_c"

foreach ($f in @($outLayout, $outStyle)) {
    if (Test-Path $f) {
        Write-Output ("OK: {0} ({1} bytes)" -f $f, (Get-Item $f).Length)
    } else {
        Write-Output "FAILED: $f"
    }
}

Write-Output ""
Write-Output "For local testing you can skip the Workshop entirely: copy the two compiled files into"
Write-Output "  game\csgo\panorama\layout\custom_game\  and  game\csgo\panorama\styles\custom_game\"
Write-Output "Your own client will load them. Restart the game after each change - Panorama caches layouts."
