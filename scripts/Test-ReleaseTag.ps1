$ErrorActionPreference = 'Stop'
$guard = Join-Path $PSScriptRoot 'Assert-ReleaseTag.ps1'
$directory = Join-Path ([IO.Path]::GetTempPath()) "release-tag-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $directory | Out-Null
function Git-Checked {
    & git @args
    if ($LASTEXITCODE) { throw "Git fixture command failed: $args" }
}
function Expect-Rejection([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch {
        if ($_.Exception.Message -like "*$Message*") { return }
        throw
    }
    throw "Expected rejection: $Message"
}
Push-Location $directory
try {
    Git-Checked init -b trunk .
    Git-Checked config user.name 'Release guard test'
    Git-Checked config user.email 'test@example.invalid'
    Git-Checked -c commit.gpgsign=false commit --allow-empty -m initial
    Git-Checked tag v1.0.0
    Git-Checked -c tag.gpgsign=false tag -a v1.0.1 -m annotated
    Git-Checked -c commit.gpgsign=false commit --allow-empty -m next
    Git-Checked branch side HEAD~1
    Git-Checked remote add origin $directory
    foreach ($tag in @('v1.0.0', 'v1.0.1')) {
        Git-Checked checkout --detach $tag
        & $guard -Ref "refs/tags/$tag" -DefaultBranch trunk
    }
    Expect-Rejection { & $guard -Ref refs/heads/trunk -DefaultBranch trunk } 'requires a tag ref'
    Git-Checked checkout side
    Git-Checked -c commit.gpgsign=false commit --allow-empty -m off-branch
    Git-Checked tag v2.0.0
    Expect-Rejection { & $guard -Ref refs/tags/v2.0.0 -DefaultBranch trunk } 'not reachable'
    Expect-Rejection { & $guard -Ref refs/tags/v1.0.0 -DefaultBranch trunk } 'checkout does not match'
    Write-Output 'Release tag guards passed: 2 valid tags, 3 rejected refs/checkouts.'
} finally {
    Pop-Location
    $resolved = [IO.Path]::GetFullPath($directory)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'release-tag-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
