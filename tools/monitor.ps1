param([int]$Samples = 13, [int]$IntervalSec = 10)

$exe = Join-Path $PSScriptRoot '..\bin\Debug\net9.0-windows10.0.19041.0\Nimbus.exe'
Get-Process Nimbus -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$sig = '[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);'
$U = Add-Type -MemberDefinition $sig -Name GuiResMon -Namespace W -PassThru

$p = Start-Process $exe -PassThru
$rows = @()
for ($i = 0; $i -lt $Samples; $i++) {
    Start-Sleep -Seconds $IntervalSec
    $p.Refresh()
    if ($p.HasExited) { $rows += "EXITED after ~$(($i)*$IntervalSec)s, code=$($p.ExitCode)"; break }
    $gdi  = $U::GetGuiResources($p.Handle, 0)
    $user = $U::GetGuiResources($p.Handle, 1)
    $rows += ("{0,4}s  Handles={1,5}  GDI={2,4}  USER={3,4}  WS={4}MB" -f (($i + 1) * $IntervalSec), $p.HandleCount, $gdi, $user, [math]::Round($p.WorkingSet64 / 1MB, 1))
}
$rows
"--- crash.log (tail) ---"
$log = Join-Path $env:LOCALAPPDATA 'Nimbus\crash.log'
if (Test-Path $log) { Get-Content $log -Tail 40 } else { 'no crash log' }
