param(
    [string] $Repository = 'simurg79/hyper-v-mcp',
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$parts = $Repository.Split('/')
if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[0]) -or
    [string]::IsNullOrWhiteSpace($parts[1])) {
    throw 'Repository must be owner/name.'
}

$rootPath = [IO.Path]::GetFullPath($Root)
$files = @(git -C $rootPath ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enumerate tracked public files.'
}
$tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    [void] $tracked.Add([IO.Path]::GetFullPath(
        (Join-Path $rootPath $file.Replace('/', [IO.Path]::DirectorySeparatorChar))))
}
$projectUrl = [regex]::new(
    'https?://github\.com/' + [regex]::Escape($parts[0]) + '/(?<repo>[A-Za-z0-9_.-]+)',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
$documentPath = [regex]::new(
    '(?<![A-Za-z0-9_.])/?(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+\.md\b')
$violations = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $path = Join-Path $rootPath $file.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes -contains 0) {
        continue
    }
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    foreach ($match in $projectUrl.Matches($text)) {
        if (-not $match.Groups['repo'].Value.Equals(
            $parts[1], [StringComparison]::OrdinalIgnoreCase)) {
            $violations.Add("${file}: project URL points outside the public repository")
        }
    }
    if ($file -eq '.gitignore') {
        continue
    }
    $withoutUrls = [regex]::Replace($text, 'https?://\S+', '')
    foreach ($match in $documentPath.Matches($withoutUrls)) {
        $reference = $match.Value
        if ($file.StartsWith('.github/') -and
            $reference -match '^\.\./blob/[^/]+/(?<target>.+)$') {
            $reference = $Matches['target']
        }
        $relative = $reference.TrimStart('/').Replace('/', [IO.Path]::DirectorySeparatorChar)
        $fromRoot = [IO.Path]::GetFullPath((Join-Path $rootPath $relative))
        $fromFile = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $path) $relative))
        if (-not $tracked.Contains($fromRoot) -and -not $tracked.Contains($fromFile)) {
            $violations.Add("${file}: documentation locator has no tracked public target")
        }
    }
}
if ($violations.Count -gt 0) {
    throw ("Public metadata check failed:`n" + (($violations | Select-Object -Unique) -join "`n"))
}
Write-Output "Public metadata check passed: $($files.Count) tracked files."
