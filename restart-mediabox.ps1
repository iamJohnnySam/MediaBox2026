# Quick Restart Script for MediaBox2026

## Stop the running application
Write-Host "Stopping MediaBox2026..." -ForegroundColor Yellow
Get-Process | Where-Object { $_.ProcessName -like "*MediaBox*" } | Stop-Process -Force
Start-Sleep -Seconds 2

## Build the application
Write-Host "Building MediaBox2026..." -ForegroundColor Cyan
dotnet build "C:\Users\johns\source\repos\MediaBox2026\MediaBox2026\MediaBox2026.csproj" --configuration Release

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful!" -ForegroundColor Green

    ## Start the application
    Write-Host "Starting MediaBox2026..." -ForegroundColor Green
    Start-Process -FilePath "dotnet" -ArgumentList "run --project C:\Users\johns\source\repos\MediaBox2026\MediaBox2026\MediaBox2026.csproj --configuration Release" -WorkingDirectory "C:\Users\johns\source\repos\MediaBox2026\MediaBox2026"

    Write-Host "MediaBox2026 started successfully!" -ForegroundColor Green
    Write-Host "Check logs for startup messages..." -ForegroundColor Cyan
} else {
    Write-Host "Build failed! Check error messages above." -ForegroundColor Red
}
