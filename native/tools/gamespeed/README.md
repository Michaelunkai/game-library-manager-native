# gamespeed - the per-game speed hook

`gamespeed.c` installs inline hooks over the wall-clock APIs a game uses to
advance its simulation (`GetTickCount`, `GetTickCount64`,
`QueryPerformanceCounter`, `timeGetTime`, `GetSystemTimeAsFileTime`,
`NtQuerySystemTime`) and scales the elapsed time each of them reports. A game
running at 2x sees twice as much time pass, so everything downstream of it -
timers, frame budgets, cooldowns, scripted waits - runs at that rate.

The clock is maintained in fixed point (`GS_FP_ONE` = 65536) and is
**monotonically non-decreasing**: a game that computes frame deltas must never
see a negative one, and slowing down necessarily pulls a fast virtual clock back
toward real time. `gs_apply_factor_locked` therefore lifts the accumulator to the
last published value on a decrease and carries the sub-tick fraction across, so
the clock stays continuous and keeps advancing at the new rate.

Exports: `GameSpeed_Set`, `GameSpeed_Get`, `GameSpeed_Reset`,
`GameSpeed_Version`.

## 32-bit: built and measured, but NOT shipped

A 64-bit DLL cannot be injected into a 32-bit game and vice versa, so a second
build is required. The WinLibs MinGW-w64 used for x86-64 is `--disable-multilib`
(`gcc -print-multi-lib` prints only `.;`), so the 32-bit build uses LLVM-MinGW
`UCRT`, which ships an i686 sysroot:

```
i686-w64-mingw32-clang -shared -O2 -o gamespeed32.dll gamespeed.c -lkernel32
```

This compiles with no diagnostics and produces a genuine
`coff-i386` / `PE32` DLL exporting all four functions. Against a real x86 child
process it installs all five hooks and applies the factor - and then **faults
`0xC0000005`**. The diagnostic trace ends at `applied factorFp=131072 scaled=1`
with no `HOOK` line, so the fault happens inside `GameSpeed_Set` after
installation rather than inside a patched API.

Because of that, `gamespeed32.dll` is **excluded from the package**
(`GameLibrary.Native.csproj`, `<None Remove="tools\gamespeed\gamespeed32.dll" />`)
and a 32-bit game is reported as an architecture mismatch instead. Refusing a
32-bit game is recoverable; crashing it is not. `GameSpeedNativeTests` asserts
both that the file is absent from the output and that the app refuses 32-bit, so
shipping the 32-bit build will fail the suite loudly rather than silently
injecting something that faults.

To resume: investigate the x86-only fault after `gs_apply_factor_locked` (the
`gs_install`/`gs_build_jump`/`gs_ilen` decoder paths are the likely suspects, as
the x86 hotpatch prologues and the ntdll syscall stub differ from x64), then
remove the `None Remove` exclusion and the two refusal assertions.

## Building (64-bit)

WinLibs MinGW-w64, as installed by the `BrechtSanders.WinLibs.MCF.UCRT` package:

```
gcc -m64 -O2 -Wall -Wextra -static-libgcc -shared -s -o gamespeed64.dll gamespeed.c -lkernel32
```

`-static-libgcc` matters on the 64-bit build: without it the DLL pulls in
`libgcc_s_seh_1.dll` and stops being self-contained. Rebuild
`gamespeed64.dll` after any change to `gamespeed.c`; a stale DLL silently lags
the source.

## Verifying

```
llvm-objdump -p gamespeed64.dll | findstr /i GameSpeed_   # expect the four exports
llvm-objdump -p gamespeed32.dll | findstr /i "file format" # expect coff-i386
```

Measuring the scale factor is subtler than it looks. Do **not** time the target
with `Stopwatch` inside the process being hooked: `Stopwatch` reads
`QueryPerformanceCounter`, which the hook has just patched, so both the
"virtual" and the "real" side come from the patched clock and the ratio is always
exactly 1.0. Measure from a process that is not injected. Expected results,
corrected for the ~3% process-startup overhead in the timing window:

| factor | measured ratio | corrected |
|---|---|---|
| 1.0 | 0.9725 | 1.000 |
| 2.0 | 1.9455 | 2.001 |
| 0.5 | 0.4870 | 0.501 |
| 4.0 | 3.8948 | 4.005 |

## Diagnostics

Compiling with `-DGS_DIAG` routes a trace to stderr covering hook installation
(the resolved real entry points, stolen length, patch length, trampoline
address, and a verification of the bytes written to each target) and every clock
read. Without `-DGS_DIAG` all tracing compiles to `((void)0)`, so the shipping
build has no diagnostics in it.
