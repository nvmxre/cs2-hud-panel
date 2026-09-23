# Builds the CS2UIKit Workshop addon: the ready-made windows (toasts, menus, votes, confirm) that plugins drive
# from C# without writing any Panorama themselves.
#
#   python kit\make_images.py        # icons (only when they change)
#   powershell -File kit\build.ps1   # finds CS2 through the Steam libraries
#   powershell -File kit\build.ps1 -Cs2 "D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"
#
# The result in game\csgo_addons\cs2uikit is what gets published to the Workshop; servers hand it to players with
# MultiAddonManager. While developing you can skip publishing: copy the compiled .vxml_c / .vcss_c into
# game\csgo\panorama\... of your own client and restart the game (Panorama caches layouts per session).
#
# Needs the Counter-Strike 2 Workshop Tools: CS2 -> Settings -> "Install Counter-Strike Workshop Tools" -> Yes.

param(
    [string]$Cs2 = "",
    [string]$Addon = "cs2uikit"
)

$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Cs2) {
    $roots = @("C:\Program Files (x86)\Steam")
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($line in (Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches)) {
            foreach ($m in $line.Matches) { $roots += $m.Groups[1].Value.Replace('\\', '\') }
        }
    }
    foreach ($r in $roots) {
        $candidate = Join-Path $r "steamapps\common\Counter-Strike Global Offensive"
        if (Test-Path $candidate) { $Cs2 = $candidate; break }
    }
}
if (-not $Cs2 -or -not (Test-Path $Cs2)) { Write-Output "CS2 not found. Pass -Cs2 <path>"; exit 1 }

$compiler = Join-Path $Cs2 "game\bin\win64\resourcecompiler.exe"
if (-not (Test-Path $compiler)) { Write-Output "Workshop Tools are not installed (no resourcecompiler.exe)"; exit 1 }

$content = Join-Path $Cs2 "content\csgo_addons\$Addon"
$game = Join-Path $Cs2 "game\csgo_addons\$Addon"
$layoutDir = Join-Path $content "panorama\layout\custom_game"
$styleDir = Join-Path $content "panorama\styles\custom_game"
$imageDir = Join-Path $styleDir "cs2uikit"
New-Item -ItemType Directory -Force -Path $layoutDir, $styleDir, $imageDir, $game | Out-Null

Copy-Item (Join-Path $src "layout\*.xml") $layoutDir -Force
Copy-Item (Join-Path $src "styles\*.css") $styleDir -Force
Copy-Item (Join-Path $src "images\*") $imageDir -Force
Copy-Item (Join-Path $src "addoninfo.txt") $game -Force

# Fresh timestamps and no old output: the compiler compares both and answers "skipped" otherwise, leaving an old
# layout in the addon while reporting success.
Get-ChildItem $layoutDir, $styleDir -File | ForEach-Object { $_.LastWriteTime = Get-Date }
Remove-Item (Join-Path $game "panorama") -Recurse -Force -ErrorAction SilentlyContinue

& $compiler -i (Join-Path $imageDir "*.vtex") -r
foreach ($f in Get-ChildItem $layoutDir -Filter *.xml) { & $compiler -i $f.FullName -f -r }
foreach ($f in Get-ChildItem $styleDir -Filter *.css) { & $compiler -i $f.FullName -f -r }

$built = Get-ChildItem (Join-Path $game "panorama") -Recurse -File -ErrorAction SilentlyContinue
Write-Output "Built $($built.Count) file(s) into $game"
