param(
    [string]$Repo = 'Tayo-Aina/SchoolPdfSorter'
)

# Rough popularity signal only: GitHub counts downloads, not people. Use the usage
# counter in tools/usage-worker for distinct installs.

$headers = @{ 'User-Agent' = 'SchoolPdfSorter' }
$releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers $headers

$rows = foreach ($release in $releases) {
    [pscustomobject]@{
        Release   = $release.tag_name
        Downloads = ($release.assets | Measure-Object -Property download_count -Sum).Sum
    }
}

$rows | Format-Table -AutoSize

$total = ($rows | Measure-Object -Property Downloads -Sum).Sum
Write-Host "Total downloads: $total"
Write-Host 'Downloads are not unique users: one person can download several times.'
