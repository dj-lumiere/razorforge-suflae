# Merge all wiki markdown files into one big file

$razorforgeWikiDir = "L:\programming\RiderProjects\RazorForge\RazorForge-Wiki\docs"
$suflaeWikiDir = "L:\programming\RiderProjects\RazorForge\Suflae-Wiki\docs"
$rfOutputFile = "L:\programming\RiderProjects\RazorForge\MERGED_WIKI_RF.md"
$sfOutputFile = "L:\programming\RiderProjects\RazorForge\MERGED_WIKI_SF.md"

# Get all markdown files from wiki directory
$rfMdFiles = Get-ChildItem $razorforgeWikiDir -Filter "*.md" | Sort-Object Name
$sfMdFiles = Get-ChildItem $suflaeWikiDir -Filter "*.md" | Sort-Object Name

# RF merging part
# Create/clear output file
"# RazorForge Wiki - Merged Documentation`n" | Out-File -FilePath $rfOutputFile -Encoding UTF8
"Generated on: $( Get-Date -Format 'yyyy-MM-dd HH:mm:ss' )`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8
"---`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8

# Counter for progress
$count = 0
$total = $rfMdFiles.Count

Write-Host "Merging $total markdown files from wiki directory..." -ForegroundColor Green

foreach ($file in $rfMdFiles)
{
    $count++
    Write-Host "[$count/$total] Processing: $( $file.Name )" -ForegroundColor Cyan

    # Add file separator with filename as heading
    "`n`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8 -NoNewline
    "# ==========================================`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8
    "# FILE: $( $file.Name )`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8
    "# ==========================================`n`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8

    # Read and append file content
    $content = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $content | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8 -NoNewline

    # Add separator
    "`n`n---`n" | Out-File -FilePath $rfOutputFile -Append -Encoding UTF8
}

Write-Host "`nMerge complete! Output file: $rfOutputFile" -ForegroundColor Green
Write-Host "Total files merged: $total" -ForegroundColor Green

# SF merging part
# Create/clear output file
"# Suflae Wiki - Merged Documentation`n" | Out-File -FilePath $sfOutputFile -Encoding UTF8
"Generated on: $( Get-Date -Format 'yyyy-MM-dd HH:mm:ss' )`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8
"---`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8

# Counter for progress
$count = 0
$total = $sfMdFiles.Count

Write-Host "Merging $total markdown files from wiki directory..." -ForegroundColor Green

foreach ($file in $sfMdFiles)
{
    $count++
    Write-Host "[$count/$total] Processing: $( $file.Name )" -ForegroundColor Cyan

    # Add file separator with filename as heading
    "`n`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8 -NoNewline
    "# ==========================================`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8
    "# FILE: $( $file.Name )`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8
    "# ==========================================`n`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8

    # Read and append file content
    $content = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $content | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8 -NoNewline

    # Add separator
    "`n`n---`n" | Out-File -FilePath $sfOutputFile -Append -Encoding UTF8
}

Write-Host "`nMerge complete! Output file: $sfOutputFile" -ForegroundColor Green
Write-Host "Total files merged: $total" -ForegroundColor Green
