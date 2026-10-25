param([string]$Model = 'gemini-3.1-flash-image')
$ErrorActionPreference = 'Stop'
$apiKey = $env:GEMINI_API_KEY
if (!$apiKey) { throw 'Set GEMINI_API_KEY before generating artwork.' }
$prompt = 'Create a polished square game mod icon, no text, no lettering, no logo or watermark. An original industrial orange diesel freight locomotive in three-quarter isometric view, with three small teal control sliders subtly integrated into the lower foreground. Dark navy background, crisp mechanical illustration, restrained amber and turquoise palette, readable silhouette at thumbnail size, generous margins. Original artwork, no existing game assets. Single icon filling a 1:1 canvas.'
$body = @{
    contents = @(@{ parts = @(@{ text = $prompt }) })
    generationConfig = @{ responseModalities = @('TEXT', 'IMAGE') }
} | ConvertTo-Json -Depth 8
$response = Invoke-RestMethod -Method Post -Uri "https://generativelanguage.googleapis.com/v1beta/models/${Model}:generateContent" -Headers @{'x-goog-api-key'=$apiKey} -ContentType 'application/json' -Body $body -TimeoutSec 180
$part = $response.candidates[0].content.parts | Where-Object { $_.inlineData.mimeType -like 'image/*' } | Select-Object -First 1
if (!$part) {
    $response | ConvertTo-Json -Depth 12 | Write-Output
    throw 'Gemini returned no image.'
}
$extension = if ($part.inlineData.mimeType -eq 'image/jpeg') { 'jpg' } else { 'png' }
$destination = Join-Path $PSScriptRoot "../assets/train-parameter-mod.$extension"
[IO.File]::WriteAllBytes($destination, [Convert]::FromBase64String($part.inlineData.data))
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile($destination)
$thumbnail = New-Object Drawing.Bitmap 256,256
$graphics = [Drawing.Graphics]::FromImage($thumbnail)
try {
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($source, 0, 0, 256, 256)
    $thumbnail.Save((Join-Path $PSScriptRoot '../assets/thumbnail.png'), [Drawing.Imaging.ImageFormat]::Png)
}
finally { $graphics.Dispose(); $thumbnail.Dispose(); $source.Dispose() }
Write-Output "Generated $destination with $Model"
if ($response.usageMetadata) { $response.usageMetadata | ConvertTo-Json -Compress }
