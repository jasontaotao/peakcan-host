param(
    [string] $Path = "$PWD/large-100mb.asc",
    [int] $Count = 2000000
)

# 每行约 47 bytes + newline；2,000,000 行约 100MB，时长约 2000 秒。
$writer = New-Object System.IO.StreamWriter($Path, $false, [System.Text.Encoding]::ASCII, 1048576)
try {
  $writer.WriteLine("date Wed Jul 1 10:00:00.000 2026")
  $writer.WriteLine("base 0x7e0 500k")
  $writer.WriteLine("internal events logged")

  $ts = 0.0
  for ($i = 0; $i -lt $Count; $i++) {
    $id = 0x100 + ($i % 8)
    $line = " {0:F6} 51  {1:X3}  8  01 02 03 04 05 06 07 08" -f $ts, $id
    $writer.WriteLine($line)
    $ts += 0.001
  }
}
finally {
  $writer.Dispose()
}

Write-Host "done: $Count frames, $([math]::Round((Get-Item $Path).Length / 1MB, 1)) MB"
