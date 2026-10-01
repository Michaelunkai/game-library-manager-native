/*
 * gamespeed.h - shared contract for the in-process game time-scale hook DLL.
 *
 * SAFETY BOUNDARY: this library exists for the user's own offline, single-player
 * games launched from this library. It does not attempt to defeat, evade or hide
 * from anti-cheat or any online-service integrity system, and the managed loader
 * refuses any process it cannot positively identify.
 *
 * The control block below is a byte-for-byte contract with GameSpeedNative.cs.
 * Every field is an 8-byte little-endian integer so that no float marshalling
 * disagreement is possible between the C and the C# side.
 */
#ifndef GAMESPEED_H
#define GAMESPEED_H

#include <windows.h>

#if defined(_WIN64)
#  define GS_ARCH_BITS 64
#else
#  define GS_ARCH_BITS 32
#endif

/* A factor of exactly 1.0 is the "normal" state selected by F3: every hook is a
   transparent pass-through and no scaling arithmetic is executed. */
#define GS_MIN_FACTOR        0.05
#define GS_MAX_FACTOR       20.00

/* Control-block factor unit: factor * 1e6, truncated. Exact for the 0.5 steps
   the F1/F2 keys apply and for every half-step the speed bar can select. */
#define GS_FACTOR_SCALE     1000000LL

/* Scaled-clock accumulator resolution: 16 fractional bits. Truncation per
   integration step is below 2^-16 of one clock tick, so drift over any realistic
   session is nanoseconds, and the accumulator still cannot overflow a 64-bit
   value for roughly 4.5 years of play at 2x. */
#define GS_FP_BITS           16
#define GS_FP_ONE            (1LL << GS_FP_BITS)

/* A real-time gap longer than this is a suspend or a resume, not elapsed
   simulation time. Expressed in seconds because the accumulator counts QPC
   ticks, whose scale depends on the machine's QueryPerformanceFrequency. */
#define GS_MAX_STEP_SECONDS   60

#define GS_BLOCK_MAGIC       0x3130544C504D5347ull  /* "GSMPLT01" */
#define GS_BLOCK_VERSION     1
#define GS_BLOCK_BYTES       96

#define GS_FLAG_HOOKS        0x1   /* at least one hook is live                      */
#define GS_FLAG_FAULTED      0x2   /* safety net restored the original bytes         */
#define GS_FLAG_DETACHED     0x4   /* the manager stopped steering; clock is normal  */
#define GS_FLAG_BLOCK        0x8   /* the control block was mapped                   */

/* The manager's heartbeat must advance at least this often, in watchdog ticks,
   or the DLL returns itself to 1.0 so a crashed or killed app cannot leave a
   game running fast forever. */
#define GS_HEARTBEAT_TIMEOUT_MS 5000
#define GS_WATCHDOG_TICK_MS     1000

typedef struct GS_BLOCK
{
    unsigned __int64 magic;               /*   0  GS_BLOCK_MAGIC                     */
    unsigned __int64 version;             /*   8  GS_BLOCK_VERSION                   */
    unsigned __int64 flags;               /*  16  GS_FLAG_*                          */
    unsigned __int64 hookCount;           /*  24  live hook count                    */
    unsigned __int64 ownerHeartbeat;      /*  32  manager-owned monotonic counter    */
    unsigned __int64 desiredFactorMicro;  /*  40  manager-owned request; 0 = normal */
    unsigned __int64 generation;          /*  48  manager-owned, +1 per request      */
    unsigned __int64 activeGeneration;     /*  56  DLL echoes the adopted generation */
    unsigned __int64 appliedFactorMicro;  /*  64  DLL echo of the factor in force   */
    unsigned __int64 virtualElapsedQpc;   /*  72  DLL: virtual elapsed, whole QPC    */
    unsigned __int64 anchorQpc;           /*  80  DLL: real QPC at install           */
    unsigned __int64 qpcFrequency;        /*  88  DLL: QPC ticks per second          */
} GS_BLOCK;

#if defined(__cplusplus)
extern "C" {
#endif

/* Fixed, minimal export surface. Returns 1 when the factor was accepted, 0 when
   it was non-finite or outside [GS_MIN_FACTOR, GS_MAX_FACTOR]; a rejected factor
   leaves the factor in force completely untouched. */
__declspec(dllexport) int          __cdecl GameSpeed_Set(double factor);
__declspec(dllexport) double       __cdecl GameSpeed_Get(void);
/* Returns the clock to 1.0 by re-basing, which introduces no time jump. */
__declspec(dllexport) void         __cdecl GameSpeed_Reset(void);
/* (major << 16) | minor | machine: 0x00010002 is x64, 0x00010001 is x86. */
__declspec(dllexport) unsigned int __cdecl GameSpeed_Version(void);

#if defined(__cplusplus)
}
#endif

#endif /* GAMESPEED_H */