param(
    [string]$Url = 'http://192.168.1.108:8012/',
    [int]$Chars = 6000
)
try {
    $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 15
    $r.Content.Substring(0, [Math]::Min($Chars, $r.Content.Length))
} catch {
    Write-Output "FETCH FAILED: $($_.Exception.Message)"
}
