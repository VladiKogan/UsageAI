param(
    [double]$MinimumLinePercent = 83,
    [double]$MinimumBranchPercent = 76
)

$ErrorActionPreference = 'Stop'
$coveragePath = Join-Path ([System.IO.Path]::GetTempPath()) "UsageAI.coverage.$PID.cobertura.xml"

try {
    dotnet build .\UsageAI.sln -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw "The Debug build failed before coverage collection."
    }

    $testCommand = 'dotnet run --project .\UsageAI.Tests\UsageAI.Tests.csproj -c Debug --no-build'
    dnx --yes dotnet-coverage -- collect -f cobertura -o $coveragePath $testCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Desktop coverage collection failed."
    }

    $report = [xml](Get-Content -LiteralPath $coveragePath -Raw)
    $package = $report.coverage.packages.package |
        Where-Object { $_.name -eq 'UsageAI' } |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "The coverage report did not contain the UsageAI package."
    }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $linePercent = [double]::Parse([string]$package.'line-rate', $culture) * 100
    $branchPercent = [double]::Parse([string]$package.'branch-rate', $culture) * 100
    Write-Host ("UsageAI coverage: {0:N2}% line, {1:N2}% branch" -f $linePercent, $branchPercent)

    if ($linePercent -lt $MinimumLinePercent) {
        throw ("UsageAI line coverage {0:N2}% is below the {1:N2}% minimum." -f $linePercent, $MinimumLinePercent)
    }
    if ($branchPercent -lt $MinimumBranchPercent) {
        throw ("UsageAI branch coverage {0:N2}% is below the {1:N2}% minimum." -f $branchPercent, $MinimumBranchPercent)
    }
}
finally {
    if (Test-Path -LiteralPath $coveragePath) {
        Remove-Item -LiteralPath $coveragePath -Force
    }
}
