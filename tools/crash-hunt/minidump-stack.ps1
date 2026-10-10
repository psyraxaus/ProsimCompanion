param([string]$Path, [int]$ThreadId = 0)
# Faulting-thread native backtrace from a minidump: ThreadList stream (3) gives the thread's
# stack bytes; every 8-byte value that lands inside a loaded module is a return-address
# candidate (crude, no unwinding — but it names the native components on the stack).
$bytes = [System.IO.File]::ReadAllBytes($Path)
$br = New-Object System.IO.BinaryReader([System.IO.MemoryStream]::new($bytes))
$null = $br.ReadUInt32(); $null = $br.ReadUInt32(); $numStreams = $br.ReadUInt32(); $dirRva = $br.ReadUInt32()
$streams = @{}
for ($i = 0; $i -lt $numStreams; $i++) { $br.BaseStream.Position = $dirRva + ($i * 12); $t = $br.ReadUInt32(); $s = $br.ReadUInt32(); $r = $br.ReadUInt32(); $streams[[int]$t] = @{ size = $s; rva = $r } }
$modules = @()
$br.BaseStream.Position = $streams[4].rva; $count = $br.ReadUInt32()
for ($i = 0; $i -lt $count; $i++) {
    $br.BaseStream.Position = $streams[4].rva + 4 + ($i * 108)
    $base = $br.ReadUInt64(); $size = $br.ReadUInt32(); $null = $br.ReadUInt32(); $null = $br.ReadUInt32(); $nameRva = $br.ReadUInt32()
    $br.BaseStream.Position = $nameRva; $len = $br.ReadUInt32(); $name = [System.Text.Encoding]::Unicode.GetString($br.ReadBytes($len))
    $modules += [pscustomobject]@{ Base = $base; Size = $size; Name = (Split-Path $name -Leaf) }
}
function Resolve-Module([uint64]$addr) { foreach ($m in $modules) { if ($addr -ge $m.Base -and $addr -lt ($m.Base + $m.Size)) { return ("{0}+0x{1:X}" -f $m.Name, ($addr - $m.Base)) } }; return $null }
if ($ThreadId -eq 0) { $br.BaseStream.Position = $streams[6].rva; $ThreadId = $br.ReadUInt32() }
$br.BaseStream.Position = $streams[3].rva; $n = $br.ReadUInt32()
$thread = $null
for ($i = 0; $i -lt $n; $i++) {
    $br.BaseStream.Position = $streams[3].rva + 4 + ($i * 48)
    $tid = $br.ReadUInt32(); $null = $br.ReadUInt32(); $null = $br.ReadUInt32(); $null = $br.ReadUInt32(); $null = $br.ReadUInt64()
    $stackStart = $br.ReadUInt64(); $stackSize = $br.ReadUInt32(); $stackRva = $br.ReadUInt32()
    $ctxSize = $br.ReadUInt32(); $ctxRva = $br.ReadUInt32()
    if ($tid -eq $ThreadId) { $thread = [pscustomobject]@{ Tid = $tid; StackStart = $stackStart; StackSize = $stackSize; StackRva = $stackRva; CtxRva = $ctxRva }; break }
}
if (-not $thread) { "thread 0x{0:X} not in thread list" -f $ThreadId; exit }
$br.BaseStream.Position = $thread.CtxRva + 0x98; $rsp = $br.ReadUInt64()
$br.BaseStream.Position = $thread.CtxRva + 0xF8; $rip = $br.ReadUInt64()
"thread 0x{0:X}: rip={1} rsp=0x{2:X} stack=[0x{3:X} +0x{4:X}]" -f $thread.Tid, (Resolve-Module $rip), $rsp, $thread.StackStart, $thread.StackSize
$startOff = [Math]::Max(0, $rsp - $thread.StackStart)
$shown = 0; $last = ""
for ($p = $startOff; $p -lt $thread.StackSize; $p += 8) {
    $br.BaseStream.Position = $thread.StackRva + $p; $v = $br.ReadUInt64()
    $m = Resolve-Module $v
    if ($m -and $m -ne $last) { "  sp+0x{0:X}: {1}" -f ($p - $startOff), $m; $last = $m; $shown++ }
    if ($shown -ge 45) { break }
}
