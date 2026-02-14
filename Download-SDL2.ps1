# Download SDL2.dll automatically
# Run this script from the MechFFB solution folder

$SDL2_VERSION = "2.30.9"
$SDL2_URL = "https://github.com/libsdl-org/SDL/releases/download/release-$SDL2_VERSION/SDL2-$SDL2_VERSION-win32-x64.zip"
$TEMP_ZIP = "SDL2.zip"
$DLL_NAME = "SDL2.dll"

Write-Host "Downloading SDL2 $SDL2_VERSION..." -ForegroundColor Cyan

try {
    # Download the zip file
    Invoke-WebRequest -Uri $SDL2_URL -OutFile $TEMP_ZIP -UseBasicParsing
    Write-Host "✓ Downloaded SDL2.zip" -ForegroundColor Green
    
    # Extract just the DLL
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($TEMP_ZIP)
    
    $dllEntry = $zip.Entries | Where-Object { $_.Name -eq $DLL_NAME }
    
    if ($dllEntry) {
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dllEntry, $DLL_NAME, $true)
        Write-Host "✓ Extracted SDL2.dll" -ForegroundColor Green
    }
    
    $zip.Dispose()
    
    # Clean up
    Remove-Item $TEMP_ZIP
    
    Write-Host ""
    Write-Host "Success! SDL2.dll is ready." -ForegroundColor Green
    Write-Host "You can now build the solution." -ForegroundColor Cyan
    
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please download SDL2 manually:" -ForegroundColor Yellow
    Write-Host "1. Go to: $SDL2_URL"
    Write-Host "2. Extract SDL2.dll"
    Write-Host "3. Place it in this folder"
}

Read-Host "Press Enter to close"
