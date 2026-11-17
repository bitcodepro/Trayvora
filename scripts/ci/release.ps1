param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
function Invoke-Gh([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE) { throw "GitHub operation failed: $($Arguments[0])" }
    return $result
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$m = Get-Content (Join-Path $ArtifactRoot 'release.json') -Raw | ConvertFrom-Json
if ($m.commit -cne $env:GITHUB_SHA -or $m.version -notmatch '^0\.1\.[0-9]+$') { throw 'Artifact/source mismatch' }
$zip = Join-Path $ArtifactRoot 'trayvora-win-x64.zip'
if ((Get-FileHash $zip).Hash.ToLowerInvariant() -cne $m.sha256) { throw 'Artifact hash mismatch' }
$repo = $env:GITHUB_REPOSITORY
if ($repo -ne 'bitcodepro/Trayvora') { throw 'Refusing publication outside the intended repository' }
$tag = "v$($m.version)"
# Do not move immutable tags or overwrite published assets on reruns.
$remote = git ls-remote --tags origin "refs/tags/$tag"
if ($LASTEXITCODE) { throw 'Cannot verify remote tag' }
if ($remote -and ($remote -split '\s+')[0] -cne $m.commit) { throw 'Existing tag points to a different commit' }
$releases = (Invoke-Gh @('api','--paginate',"repos/$repo/releases",'--jq','.[].tag_name'))
$exists = @($releases) -contains $tag
if (!$exists) {
    Invoke-Gh @('release','create',$tag,'--repo',$repo,'--target',$m.commit,'--draft','--title',"Trayvora $($m.version)",'--notes',"Windows x64 portable build. Source: $($m.commit). Live authenticated AI acceptance tests are not run in CI.") | Out-Null
}
$release = (Invoke-Gh @('release','view',$tag,'--repo',$repo,'--json','isDraft,assets')) | ConvertFrom-Json
if (!$release.isDraft -and !$exists) { throw 'Unexpected release state' }
foreach($name in @('trayvora-win-x64.zip','SHA256SUMS.txt','release.json')) {
    $local = Join-Path $ArtifactRoot $name
    $asset = @($release.assets | Where-Object name -eq $name)
    if ($asset.Count -eq 0) {
        if (!$release.isDraft) { throw 'Published release is missing an expected asset; do not modify it automatically' }
        Invoke-Gh @('release','upload',$tag,$local,'--repo',$repo) | Out-Null
    }
}
# Download everything back and compare to tested artifact, including retries after partial failure.
$verify = Join-Path $env:RUNNER_TEMP ("release-verification-" + [guid]::NewGuid())
New-Item -ItemType Directory $verify | Out-Null
Invoke-Gh @('release','download',$tag,'--repo',$repo,'--dir',$verify) | Out-Null
foreach($name in @('trayvora-win-x64.zip','SHA256SUMS.txt','release.json')) {
    if ((Get-FileHash (Join-Path $ArtifactRoot $name)).Hash -ne (Get-FileHash (Join-Path $verify $name)).Hash) { throw "Existing/uploaded asset mismatch: $name. Nothing overwritten." }
}
if ($release.isDraft) {
    # Every push receives its own release. Older runs completing late must not replace latest.
    $head = (Invoke-Gh @('api',"repos/$repo/git/ref/heads/main",'--jq','.object.sha')).Trim()
    $latest = if ($head -ceq $m.commit) { '--latest' } else { '--latest=false' }
    Invoke-Gh @('release','edit',$tag,'--repo',$repo,'--draft=false',$latest) | Out-Null
}
Invoke-Gh @('release','view',$tag,'--repo',$repo,'--json','url,isDraft,tagName')
