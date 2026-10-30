# Fix-NinjaTrader.ps1
# Repairs Documents\NinjaTrader 8\bin\Custom after the OneDrive mix-up:
#   1. Backs up bin\Custom to C:\NT8-Fix first (nothing is deleted).
#   2. Moves any "-DESKTOP-CGLRGJL" duplicate files out of bin\Custom (into C:\NT8-Fix).
#   3. Copies NinjaTrader files deleted in the last 3 days back from the Recycle Bin
#      into the local NinjaTrader 8 folder, skipping duplicates and never overwriting.
#      The Recycle Bin is left untouched as an extra backup.
#   4. Makes sure the AddOns and Strategies folders exist.
#
# Run with NinjaTrader CLOSED:
#   powershell -ExecutionPolicy Bypass -File "C:\NT8-Fix\Fix-NinjaTrader.ps1"

$ErrorActionPreference = 'Stop'

$DuplicateTag = 'DESKTOP-CGLRGJL'
$Local        = Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8'
$Custom       = Join-Path $Local 'bin\Custom'
$FixDir       = 'C:\NT8-Fix'
$Stamp        = Get-Date -Format 'yyyyMMdd-HHmmss'

function Say($text, $color = 'White') { Write-Host $text -ForegroundColor $color }

if (Get-Process -Name 'NinjaTrader' -ErrorAction SilentlyContinue) {
	Say 'NinjaTrader is still running. Close it (Task Manager > End task) and run this again.' Red
	exit 1
}
if (-not (Test-Path $Custom)) {
	Say "Could not find $Custom - nothing changed." Red
	exit 1
}

New-Item -ItemType Directory -Force -Path $FixDir | Out-Null

# 1. Backup
$backup = Join-Path $FixDir "Custom-backup-$Stamp"
Say "Backing up bin\Custom to $backup ..." Cyan
Copy-Item -Path $Custom -Destination $backup -Recurse

# 2. Move duplicate conflict copies out of the way
$dupDir = Join-Path $FixDir "duplicates-$Stamp"
$dups = @(Get-ChildItem -Path $Custom -Recurse -File -Filter "*$DuplicateTag*")
foreach ($d in $dups) {
	$rel    = $d.FullName.Substring($Custom.Length).TrimStart('\')
	$target = Join-Path $dupDir $rel
	New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
	Move-Item -Path $d.FullName -Destination $target
}
Say "Moved $($dups.Count) duplicate file(s) to $dupDir" Cyan

# 3. Find NinjaTrader items in the Recycle Bin
$shell   = New-Object -ComObject Shell.Application
$recycle = $shell.Namespace(0xA)
$cutoff  = (Get-Date).AddDays(-3)
$found   = @()

foreach ($item in $recycle.Items()) {
	try { $from = [string]$item.ExtendedProperty('System.Recycle.DeletedFrom') } catch { continue }
	if ($from -notmatch '\\NinjaTrader 8(\\.*)?$') { continue }

	# If the deletion date can't be read, include the item; you confirm the list below anyway.
	$deleted = Get-Date
	try { $deleted = [datetime]$item.ExtendedProperty('System.Recycle.DateDeleted') } catch { }
	if ($deleted -lt $cutoff) { continue }

	$rel  = $Matches[1]                     # e.g. \bin\Custom\Indicators
	$name = $item.Name
	if (-not $item.IsFolder) {
		# Explorer may hide extensions; the recycled file keeps the real one.
		$ext = [IO.Path]::GetExtension($item.Path)
		if ($ext -and -not $name.EndsWith($ext, [StringComparison]::OrdinalIgnoreCase)) { $name += $ext }
	}
	if ($name -like "*$DuplicateTag*") { continue }

	$found += [pscustomobject]@{
		Name     = $name
		Source   = $item.Path
		IsFolder = $item.IsFolder
		Dest     = Join-Path ($Local + $rel) $name
		Deleted  = $deleted
	}
}

if ($found.Count -eq 0) {
	Say 'No recently deleted NinjaTrader files found in the Recycle Bin.' Yellow
} else {
	Say "`nFound $($found.Count) NinjaTrader item(s) in the Recycle Bin:" Cyan
	$found | Sort-Object Dest | ForEach-Object { Say ("  {0}  {1}" -f $_.Deleted.ToString('dd/MM HH:mm'), $_.Dest.Substring($Local.Length)) }

	$answer = Read-Host "`nCopy these back into $Local ? Existing files are never overwritten. (Y/N)"
	if ($answer -match '^[Yy]') {
		$copied = 0; $skipped = 0
		foreach ($f in $found) {
			$sourceFiles = if ($f.IsFolder) {
				Get-ChildItem -Path $f.Source -Recurse -File | ForEach-Object {
					[pscustomobject]@{ From = $_.FullName; To = Join-Path $f.Dest $_.FullName.Substring($f.Source.Length).TrimStart('\') }
				}
			} else {
				[pscustomobject]@{ From = $f.Source; To = $f.Dest }
			}

			if ($f.IsFolder) { New-Item -ItemType Directory -Force -Path $f.Dest | Out-Null }

			foreach ($s in @($sourceFiles)) {
				if ($s.To -like "*$DuplicateTag*") { continue }
				if (Test-Path $s.To) { $skipped++; continue }
				New-Item -ItemType Directory -Force -Path (Split-Path $s.To) | Out-Null
				Copy-Item -Path $s.From -Destination $s.To
				$copied++
			}
		}
		Say "Restored $copied file(s); skipped $skipped that already existed." Green
	} else {
		Say 'Skipped restoring from the Recycle Bin.' Yellow
	}
}

# 4. Folders NinjaTrader expects
foreach ($folder in 'AddOns', 'Strategies') {
	$path = Join-Path $Custom $folder
	if (-not (Test-Path $path)) {
		New-Item -ItemType Directory -Path $path | Out-Null
		Say "Created missing folder bin\Custom\$folder" Cyan
	}
}

$twmc = @(Get-ChildItem -Path $Custom -Recurse -File -Filter '*TWMC*').Count
Say "`nDone. TWMC files now in bin\Custom: $twmc" Green
Say 'Next: open NinjaTrader, then Control Center > Workspaces > Open Workspace > Nathn.' Green
Say 'Do NOT press F5 / compile yet. Backup and duplicates are in C:\NT8-Fix.' Green
Read-Host "`nPress Enter to close"
