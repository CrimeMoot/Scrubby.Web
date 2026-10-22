# Starts the Scrubby.Web site locally on http://localhost:5034 (development helper).
# Requires the .NET 9 SDK; all data is read from the published asset-data branch on GitHub.
$ErrorActionPreference = "Stop"

$env:ASPNETCORE_ENVIRONMENT = "Development"

dotnet run --project Scrubby.Web --no-launch-profile --urls "http://localhost:5034"
