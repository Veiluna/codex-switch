param([Parameter(Mandatory = $true)][string]$ReleaseNotesFile,
    [string]$DistPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
$repository = 'Veiluna/codex-switch'
$metadata = Get-Content -LiteralPath (Join-Path $DistPath 'build-info.json') -Raw | ConvertFrom-Json
$version = $metadata.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid build version.' }
$tag = 'v' + $version
$name = 'CodexSwitch-Setup-' + $version + '-x64.exe'
$file = Get-Item -LiteralPath (Join-Path $DistPath $name)
$digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$record = @($metadata.Artifacts | Where-Object { $_.Name -ceq $name })
if ($record.Count -ne 1 -or $record[0].SHA256 -ine $digest) { throw 'Installer differs from its build record.' }

$root = Split-Path $PSScriptRoot -Parent
$inputs = @($metadata.Inputs | ForEach-Object { $_.Path })
if ($inputs.Count -eq 0) { throw 'Missing build input records.' }
git -C $root ls-files --error-unmatch -- $inputs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'One or more build inputs are not tracked.' }
git -C $root diff --quiet HEAD -- $inputs
if ($LASTEXITCODE -ne 0) { throw 'Commit the build inputs before publishing.' }
foreach ($inputRecord in $metadata.Inputs) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $inputRecord.Path)).Hash -ine $inputRecord.SHA256) {
        throw ('Source changed after build: ' + $inputRecord.Path)
    }
}
$commit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not read the local commit.' }
$published = gh api "repos/$repository/branches/main" --jq .commit.sha
if ($LASTEXITCODE -ne 0 -or $published -ne $commit) { throw 'Push the release commit to main before publishing.' }
$notes = Get-Item -LiteralPath $ReleaseNotesFile -ErrorAction Stop
$ref = 'refs/tags/' + $tag
function Get-RemoteTagCommit {
    $refs = @(git ls-remote --tags "https://github.com/$repository.git" $ref ($ref + '^{}'))
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the remote tag.' }
    # For annotated tags Git returns the tag object and its peeled commit.
    foreach ($line in $refs) { if ($line.EndsWith($ref + '^{}')) { return $line.Split([char]9)[0] } }
    foreach ($line in $refs) { if ($line.EndsWith($ref)) { return $line.Split([char]9)[0] } }
}
$remoteCommit = Get-RemoteTagCommit
if ($remoteCommit -and $remoteCommit -ne $commit) { throw 'Version tag already points to a different commit.' }
if (!$remoteCommit) {
    gh api --silent --method POST "repos/$repository/git/refs" -f "ref=$ref" -f "sha=$commit"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the release tag.' }
}
$releases = gh release list --repo $repository --json tagName,isDraft --limit 100 | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not check for an existing release draft.' }
$existing = $releases | Where-Object { $_.tagName -ceq $tag }
if ($existing -and !$existing.isDraft) { throw 'This version is already public.' }
if (!$existing) {
    gh release create $tag --repo $repository --verify-tag --target $commit --title $tag --draft --notes-file $notes.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the release draft.' }
}
$draft = gh release view $tag --repo $repository --json assets,targetCommitish,isDraft | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$draft.isDraft -or $draft.targetCommitish -ne $commit) { throw 'Draft targets an unexpected commit.' }
if ($draft.assets.Count -eq 0) {
    gh release upload $tag $file.FullName --repo $repository
    if ($LASTEXITCODE -ne 0) { throw 'Could not upload the installer.' }
}
$release = gh release view $tag --repo $repository --json name,tagName,isDraft,assets,targetCommitish | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$release.isDraft -or $release.name -cne $tag -or $release.tagName -cne $tag -or
    $release.targetCommitish -ne $commit -or $release.assets.Count -ne 1 -or $release.assets[0].name -cne $name -or
    $release.assets[0].digest -ine ('sha256:' + $digest) -or $release.assets[0].size -ne $file.Length) {
    throw 'Release draft failed verification.'
}
if ((Get-RemoteTagCommit) -ne $commit) { throw 'The remote tag no longer matches the release commit.' }
gh release edit $tag --repo $repository --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the verified release draft.' }
