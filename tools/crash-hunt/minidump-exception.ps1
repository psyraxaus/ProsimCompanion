param([string]$Path)
# Reads the MINIDUMP exception stream (type 6) and module list (type 4) — enough to name the
# faulting thread, the exception code and the module the faulting address falls in.
$bytes = [System.IO.File]::ReadAllBytes($Path)
$br = New-Object System.IO.BinaryReader([System.IO.MemoryStream]::new($bytes))
$sig = $br.ReadUInt32(); $ver = $br.ReadUInt32(); $numStreams = $br.ReadUInt32(); $dirRva = $br.ReadUInt32()
"signature=0x{0:X} streams={1}" -f $sig, $numStreams
$streams = @{}
for ($i = 0; $i -lt $numStreams; $i++) {
    $br.BaseStream.Position = $dirRva + ($i * 12)
    $type = $br.ReadUInt32(); $size = $br.ReadUInt32(); $rva = $br.ReadUInt32()
    $streams[[int]$type] = @{ size = $size; rva = $rva }
}
# Modules (type 4): count, then entries of 108 bytes: BaseOfImage(8) SizeOfImage(4) CheckSum(4) TimeDateStamp(4) ModuleNameRva(4) ...
$modules = @()
if ($streams.ContainsKey(4)) {
    $br.BaseStream.Position = $streams[4].rva
    $count = $br.ReadUInt32()
    for ($i = 0; $i -lt $count; $i++) {
        $br.BaseStream.Position = $streams[4].rva + 4 + ($i * 108)
        $base = $br.ReadUInt64(); $size = $br.ReadUInt32(); $null = $br.ReadUInt32(); $null = $br.ReadUInt32(); $nameRva = $br.ReadUInt32()
        $br.BaseStream.Position = $nameRva
        $len = $br.ReadUInt32()
        $name = [System.Text.Encoding]::Unicode.GetString($br.ReadBytes($len))
        $modules += [pscustomobject]@{ Base = $base; Size = $size; Name = $name }
    }
}
function Resolve-Module([uint64]$addr) {
    foreach ($m in $modules) { if ($addr -ge $m.Base -and $addr -lt ($m.Base + $m.Size)) { return ("{0}+0x{1:X}" -f (Split-Path $m.Name -Leaf), ($addr - $m.Base)) } }
    return "unknown"
}
if (-not $streams.ContainsKey(6)) { "no exception stream"; exit }
$br.BaseStream.Position = $streams[6].rva
$threadId = $br.ReadUInt32(); $null = $br.ReadUInt32()
$code = $br.ReadUInt32(); $flags = $br.ReadUInt32(); $record = $br.ReadUInt64(); $addr = $br.ReadUInt64()
$numParams = $br.ReadUInt32(); $null = $br.ReadUInt32()
$params = @(); for ($i = 0; $i -lt 15; $i++) { $params += $br.ReadUInt64() }
"faulting thread id: 0x{0:X} ({0})" -f $threadId
"exception code: 0x{0:X8}" -f $code
"exception address: 0x{0:X} -> {1}" -f $addr, (Resolve-Module $addr)
if ($numParams -gt 0) { "params: " + (($params[0..($numParams-1)] | ForEach-Object { "0x{0:X}" -f $_ }) -join ", ") }
# Thread context (CONTEXT, x64): Rip at offset 0xF8, Rsp at 0x98 within the context record
$ctxRva = $br.ReadUInt32()  # ThreadContext.DataSize comes first... re-read properly
$br.BaseStream.Position = $streams[6].rva + 8 + 152   # MINIDUMP_EXCEPTION is 152 bytes
$ctxSize = $br.ReadUInt32(); $ctxRva = $br.ReadUInt32()
$br.BaseStream.Position = $ctxRva + 0xF8; $rip = $br.ReadUInt64()
$br.BaseStream.Position = $ctxRva + 0x98; $rsp = $br.ReadUInt64()
"rip: 0x{0:X} -> {1}" -f $rip, (Resolve-Module $rip)
# Walk the stack memory for return addresses that land inside modules (crude backtrace).
# Memory64 list (type 9): count(8) baseRva(8) then {StartOfMemory(8) DataSize(8)} entries.
$found = @()
if ($streams.ContainsKey(9)) {
    $br.BaseStream.Position = $streams[9].rva
    $n = $br.ReadUInt64(); $baseRva = $br.ReadUInt64()
    $ranges = @(); $cursor = $baseRva
    for ($i = 0; $i -lt $n; $i++) { $s = $br.ReadUInt64(); $sz = $br.ReadUInt64(); $ranges += [pscustomobject]@{ Start = $s; Size = $sz; Rva = $cursor }; $cursor += $sz }
    $r = $ranges | Where-Object { $rsp -ge $_.Start -and $rsp -lt ($_.Start + $_.Size) } | Select-Object -First 1
    if ($r) {
        $off = $r.Rva + ($rsp - $r.Start); $limit = [Math]::Min(64KB, ($r.Start + $r.Size) - $rsp)
        for ($p = 0; $p -lt $limit; $p += 8) {
            $br.BaseStream.Position = $off + $p; $v = $br.ReadUInt64()
            $m = Resolve-Module $v
            if ($m -ne "unknown") { $found += ("{0}" -f $m) }
            if ($found.Count -ge 40) { break }
        }
    }
}
"---- return-address candidates on the faulting stack (top first) ----"
$found
"---- modules of interest ----"
$modules | Where-Object { $_.Name -match 'dinput|winmm|VoicemeeterRemote|onnx|Prosim|NAudio|SimConnect|ucrtbase|ntdll' } | ForEach-Object { Split-Path $_.Name -Leaf }
