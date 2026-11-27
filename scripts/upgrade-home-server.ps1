<#
.SYNOPSIS
  Replaces the running Link Cleaner Docker container with a build of this folder, keeping its SQLite config.

.DESCRIPTION
  A guided, 7-stage upgrade for a Windows host running Docker (Linux containers).
  Put the repo on the server (this script lives in its scripts\ folder), then run:
      powershell -ExecutionPolicy Bypass -File .\scripts\upgrade-home-server.ps1
  Nothing is cloned or pulled: the folder as it stands is what gets built.
  The old container is stopped, never deleted, and its /data is backed up first.
#>
$ErrorActionPreference = 'Stop'
$TotalStages = 7
$script:StageIndex = 0
$repoDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# -- Wizard helpers ---------------------------------------------------------
function Stage($name) {
    Clear-Host
    $script:StageIndex++
    Write-Host ""
    Write-Host ("> Stage {0}/{1} - {2}" -f $script:StageIndex, $TotalStages, $name) -ForegroundColor Cyan
}
function Say($m)  { Write-Host "  $m" }
function Note($m) { Write-Host "  $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "  ! $m" -ForegroundColor Yellow }
function Pause-Wizard($m = 'Press Enter to continue') { [void](Read-Host "  $m") }
function Confirm-Wizard($q) { (Read-Host "  ? $q [y/N]") -match '^[Yy]' }
function Fail($m) { Warn $m; exit 1 }

# Native commands don't throw on failure in PowerShell, so check the exit code ourselves.
function Invoke-Docker { & docker @args; if ($LASTEXITCODE -ne 0) { throw "docker $($args -join ' ') failed (exit $LASTEXITCODE)" } }
function Compose([string[]]$ComposeArgs) {
    Push-Location $repoDir
    try { & docker compose @ComposeArgs; if ($LASTEXITCODE -ne 0) { throw "docker compose $($ComposeArgs -join ' ') failed (exit $LASTEXITCODE)" } }
    finally { Pop-Location }
}
function Write-NoBom($path, $text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }

# -- Opening ----------------------------------------------------------------
Clear-Host
Write-Host "`n  Replace the running Link Cleaner container (keeping its SQLite config)" -ForegroundColor Cyan
Write-Host "  $TotalStages stages. Stop any time with Ctrl-C; nothing is deleted.`n" -ForegroundColor DarkGray
Pause-Wizard 'Ready to start?'

# -- 1 ----------------------------------------------------------------------
Stage 'Check the setup and find the running container'
foreach ($f in 'compose.yaml', 'Dockerfile') {
    if (-not (Test-Path (Join-Path $repoDir $f))) { Fail "$f not found in $repoDir. Run this script from the repo's scripts\ folder." }
}
Say "Building from: $repoDir"
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail "docker isn't installed or isn't on PATH." }
& docker compose version *> $null
if ($LASTEXITCODE -ne 0) { Fail "'docker compose' (v2) is required." }
$os = (& docker info --format '{{.OSType}}' 2>$null)
if ($LASTEXITCODE -ne 0) { Fail "Can't talk to Docker. Is Docker running, and is this an elevated or docker-users session?" }
if ($os -ne 'linux') { Fail "Docker is in '$os' container mode; this bot's image is Linux. Switch Docker Desktop to Linux containers." }
Say 'Containers on this machine:'
Write-Host ''
& docker ps -a --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}' | ForEach-Object { "    $_" }
Write-Host ''
$old = (Read-Host '  Name of the current Link Cleaner container').Trim()
& docker inspect $old *> $null
if ($LASTEXITCODE -ne 0) { Fail "No container called '$old'." }
Say "Found it: $old"

# -- 2 ----------------------------------------------------------------------
Stage 'Locate its data (this is what we must not lose)'
$mounts = (& docker inspect --format '{{json .Mounts}}' $old) | ConvertFrom-Json
$data = @($mounts) | Where-Object { $_.Destination -eq '/data' } | Select-Object -First 1
if (-not $data) {
    Warn "'$old' has nothing mounted at /data, so its database lives inside the container itself."
    Say "Replacing it would lose it. Rescue it first:  docker cp ${old}:/data .\linkcleaner-data-rescue"
    Say 'Then re-run this wizard.'
    exit 1
}
$reuseVolume = $data.Type -eq 'volume'
if ($reuseVolume) {
    Say "Docker volume: $($data.Name)"
    Note 'The new container will be pointed at this exact volume (not a fresh one).'
} else {
    Say "Host folder: $($data.Source)"
    Note "Bind mounts don't translate reliably between Windows and Docker's VM, so the data will be"
    Note 'restored from the backup into a managed Docker volume instead. The old folder is left untouched.'
}
Pause-Wizard 'Does that look right? Press Enter to continue (Ctrl-C to stop).'

# -- 3 ----------------------------------------------------------------------
Stage 'Token and storage wiring'
$envVars = (& docker inspect --format '{{json .Config.Env}}' $old) | ConvertFrom-Json
$oldToken = (@($envVars) | Where-Object { $_ -like 'DISCORD_TOKEN=*' } | Select-Object -First 1) -replace '^DISCORD_TOKEN=', ''
if ($oldToken -and (Confirm-Wizard 'Reuse the Discord token from the running container?')) {
    $token = $oldToken
} else {
    Say 'Discord developer portal > your application > Bot > Reset Token / Copy'
    Start-Process 'https://discord.com/developers/applications'
    $secure = Read-Host '  Paste the bot token' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
$envFile = Join-Path $repoDir '.env'
$kept = if (Test-Path $envFile) { @(Get-Content $envFile | Where-Object { $_ -notmatch '^DISCORD_TOKEN=' }) } else { @() }
# -join binds looser than +, so join first, then add the trailing newline.
Write-NoBom $envFile (((@($kept) + "DISCORD_TOKEN=$token") -join "`n") + "`n")
Remove-Variable token, oldToken -ErrorAction SilentlyContinue
Say "Wrote DISCORD_TOKEN to $envFile"
$overrideFile = Join-Path $repoDir 'compose.override.yaml'
if ($reuseVolume) {
    $override = "# Written by the upgrade wizard: keep using the existing data volume.`nvolumes:`n  linkcleaner-data:`n    name: `"$($data.Name)`"`n    external: true`n"
    Write-NoBom $overrideFile $override
    Say 'Wrote compose.override.yaml (points at the existing volume).'
} else {
    if (Test-Path $overrideFile) { Remove-Item $overrideFile }
    Say "No override needed: the data will be restored into compose's own volume."
}
Compose @('config', '-q')
Say 'Compose configuration is valid.'

# -- 4 ----------------------------------------------------------------------
Stage 'Build the new image (the old bot keeps running)'
try { Compose @('build') } catch { Fail "Build failed. Nothing has been changed; the old container is still running.`n  $_" }
Say 'Image built.'

# -- 5 ----------------------------------------------------------------------
Stage 'Stop the old container and back up its data'
Say 'The bot goes offline from here until the next stage finishes (usually under a minute).'
Say 'Both bots must not run on one token at once, so the old one is stopped first.'
if (-not (Confirm-Wizard "Stop '$old' now?")) { Say 'Nothing was changed.'; exit 0 }
function Restore-Old { Invoke-Docker update --restart=unless-stopped $old | Out-Null; Invoke-Docker start $old | Out-Null }
Invoke-Docker update --restart=no $old | Out-Null
Invoke-Docker stop $old | Out-Null
$backup = Join-Path $env:USERPROFILE ("linkcleaner-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
try { Invoke-Docker cp "${old}:/data/." $backup }
catch { Warn "Backup failed; restarting the old container.`n  $_"; Restore-Old; exit 1 }
Say "Backup: $backup"
Get-ChildItem $backup | ForEach-Object { "    {0,10:N0}  {1}" -f $_.Length, $_.Name }

# -- 6 ----------------------------------------------------------------------
Stage 'Start the new container'
try {
    if (-not $reuseVolume) {
        Say 'Restoring the backup into the new volume...'
        Compose @('up', '--no-start')
        $image = (& docker compose --project-directory $repoDir config --images | Select-Object -First 1)
        $vol = (& docker compose --project-directory $repoDir config --format json | ConvertFrom-Json).volumes.'linkcleaner-data'.name
        Invoke-Docker run --rm --user root -v "${vol}:/data" -v "${backup}:/backup:ro" --entrypoint sh $image -c 'cp -a /backup/. /data/ && chown -R $APP_UID /data'
    }
    Compose @('up', '-d')
} catch {
    Warn "The new container failed to start. Putting the old one back.`n  $_"
    Restore-Old
    exit 1
}
Say 'Started.'

# -- 7 ----------------------------------------------------------------------
Stage 'Verify the database came across'
Say 'Files in the data volume the new container sees:'
try { & docker compose --project-directory $repoDir run --rm --no-deps --entrypoint ls linkcleaner -la /data | ForEach-Object { "    $_" } } catch {}
Write-Host ''
Say 'Recent logs:'
try { & docker compose --project-directory $repoDir logs --tail 15 linkcleaner | ForEach-Object { "    $_" } } catch {}
Write-Host ''
Say '* In Discord, run /linkcleaner status and check your sites, mod log and ping settings match what you had.'
Pause-Wizard 'Everything look right? Press Enter.'
Note "Rollback if something is wrong:  cd $repoDir; docker compose down; docker start $old"
Note "Once you're happy:  docker rm $old   (your backup stays in $backup)"
Write-Host "`n  Upgrade complete`n" -ForegroundColor Green
