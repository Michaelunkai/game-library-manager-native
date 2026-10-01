/*
 * gamespeed.c - in-process time-scale hook DLL.
 *
 * TECHNIQUE
 *   Once loaded into a game, this DLL installs inline detours over the wall-clock
 *   APIs a game uses to advance its own simulation: GetTickCount, GetTickCount64,
 *   QueryPerformanceCounter, timeGetTime (winmm) and GetSystemTimeAsFileTime /
 *   NtQuerySystemTime.
 *
 *   The hooks return a SCALED CLOCK: monotonically increasing values whose DELTAS
 *   are multiplied by the current factor while their ABSOLUTE ORIGINS are
 *   preserved, so a game that calibrates QueryPerformanceFrequency still sees a
 *   sane counter and only the passage of time is scaled.
 *
 *   The integrator keeps, under one lock:
 *
 *       realLastQpc - the previous real reference sample, in QPC counts
 *       virtualFp   - accumulated scaled elapsed time, QPC counts * 2^16
 *       factorFp    - the factor in force, factor * 2^16
 *
 *   and every read does virtualFp += (realNow - realLastQpc) * factorFp.
 *
 * RE-BASE ON FACTOR CHANGE (the single most important correctness detail)
 *   A naive implementation computes the next delta from the OLD anchor with the
 *   NEW factor, which retroactively scales the entire interval the game slept
 *   before the key press and produces a visible time jump. gs_apply_factor_locked
 *   instead settles the OLD factor exactly up to the instant of the change and
 *   then re-bases realLastQpc to that same instant, so the first post-change
 *   interval is the only one charged at the new rate and the clock advances by
 *   zero ticks at the moment of the change.
 *
 * ORIGINAL-POINTER DISCIPLINE
 *   Every hook and every internal read reaches the real clock only through
 *   pointers captured before any patch is written. Nothing here may call a hooked
 *   API through a normal import, including the watchdog thread, or it would
 *   measure its own virtual clock and never notice a lapse of real time.
 *
 * NO THIRD-PARTY CODE: the detour engine below is self-contained.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include "gamespeed.h"

#if defined(GS_DIAG)
static void gs_trace(const char *fmt, ...)
{
    char line[512];
    va_list ap;
    va_start(ap, fmt);
    int used = _vsnprintf(line, sizeof(line) - 2, fmt, ap);
    va_end(ap);
    if (used < 0) used = 0;
    line[used] = '\r'; line[used + 1] = '\n'; line[used + 2] = 0;
    DWORD written = 0;
    HANDLE err = GetStdHandle(STD_ERROR_HANDLE);
    if (err != INVALID_HANDLE_VALUE && err != NULL) WriteFile(err, line, (DWORD)used + 2, &written, NULL);
    OutputDebugStringA(line);
}
#define GS_TRACE(...) gs_trace(__VA_ARGS__)
#else
#define GS_TRACE(...) ((void)0)
#endif

/* ------------------------------------------------------------------ state */

typedef DWORD       (WINAPI *GS_GetTickCountFn)(void);
typedef ULONGLONG   (WINAPI *GS_GetTickCount64Fn)(void);
typedef BOOL        (WINAPI *GS_QpcFn)(LARGE_INTEGER *);
typedef BOOL        (WINAPI *GS_FrequencyFn)(LARGE_INTEGER *);
typedef DWORD       (WINAPI *GS_TimeGetTimeFn)(void);
typedef void        (WINAPI *GS_FileTimeFn)(LPFILETIME);

static GS_QpcFn          g_realQpc   = NULL;
static GS_FrequencyFn    g_realFreq  = NULL;
static GS_GetTickCount64Fn g_realTick64 = NULL;
static GS_GetTickCountFn g_realTick32 = NULL;
static GS_TimeGetTimeFn  g_realMidi  = NULL;
static GS_FileTimeFn     g_realFt    = NULL;
static GS_FileTimeFn     g_realNt    = NULL;

static CRITICAL_SECTION g_lock;
static LONG             g_lockReady = 0;
static LONG             g_shuttingDown = 0;

static volatile LONG g_scaled = 0;   /* 0 => factor is exactly 1.0: pass-through */

static LONGLONG   g_factorFp    = GS_FP_ONE;
static LONGLONG   g_realLastQpc = 0;
static LONGLONG   g_virtualFp   = 0;
static LONGLONG   g_anchorQpc   = 0;
static LONGLONG   g_freq        = 0;
static LONGLONG   g_anchorFt100 = 0;
static ULONGLONG g_tickBase64  = 0;
static DWORD      g_tickBase32  = 0;
static DWORD      g_midiBase    = 0;

static HANDLE        g_blockMap    = NULL;
static GS_BLOCK     *g_block       = NULL;
static LONGLONG      g_lastBeat    = 0;
static LONGLONG      g_beatRealQpc = 0;
static HANDLE        g_stopEvent   = NULL;
static HANDLE        g_watchThread = NULL;

static void gs_enter(void)
{
    if (InterlockedCompareExchange(&g_lockReady, 1, 1) == 1) EnterCriticalSection(&g_lock);
}

static void gs_leave(void)
{
    if (InterlockedCompareExchange(&g_lockReady, 1, 1) == 1) LeaveCriticalSection(&g_lock);
}

/* ------------------------------------------------------- real-clock source */

static LONGLONG gs_now_real_qpc(void)
{
    LARGE_INTEGER c;
    if (g_realQpc != NULL && g_realQpc(&c)) return c.QuadPart;
    if (g_realTick64 != NULL && g_freq > 0) return (LONGLONG)(g_realTick64() * (g_freq / 10));
    if (g_realTick32 != NULL && g_freq > 0) return (LONGLONG)(g_realTick32() * (g_freq / 1000));
    return 0;
}

static void gs_integrate(LONGLONG realNowQpc)
{
    LONGLONG d = realNowQpc - g_realLastQpc;
    if (d <= 0)
    {
        /* A sample older than the previous one must never rewind the clock. */
        if (d < 0) g_realLastQpc = realNowQpc;
        return;
    }
    /* A wall-clock gap longer than this many seconds is a suspend or a resume, not
       elapsed simulation time, and must never be caught up at the accelerated
       rate. This is a real-time duration converted to QPC counts, NOT a
       fixed-point constant: clamping against GS_FP_ONE would cap every interval at
       65536 counts (6.5 ms at 10 MHz) and silently pin the clock near real time. */
    {
        LONGLONG cap = g_freq * GS_MAX_STEP_SECONDS;
        if (cap > 0 && d > cap) d = cap;
    }
    g_virtualFp += d * g_factorFp;
    g_realLastQpc += d;
}

static LONGLONG gs_elapsed_100ns_locked(void)
{
    /* Virtual elapsed time since install, in 100ns units. The anchor is applied per
       clock domain by the caller, never here: a GetTickCount value is an offset from
       boot, not from 1601, so adding the FILETIME anchor would produce garbage. */
    gs_integrate(gs_now_real_qpc());
#if defined(__SIZEOF_INT128__)
    return (LONGLONG)(((__int128)g_virtualFp * (LONGLONG)10000000)
                      / ((__int128)g_freq * GS_FP_ONE));
#else
    LONGLONG whole = g_virtualFp >> GS_FP_BITS;
    LONGLONG frac  = g_virtualFp & (GS_FP_ONE - 1);
    return (whole / g_freq) * 10000000
         + ((whole % g_freq) * 10000000) / g_freq
         + ((frac / g_freq) * 10000000) / GS_FP_ONE;
#endif
}

static LONGLONG gs_snapshot_qpc_locked(void)
{
    gs_integrate(gs_now_real_qpc());
    return g_anchorQpc + (g_virtualFp >> GS_FP_BITS);
}

/* Absolute FILETIME-domain value: the real date and time of day at install plus
   the virtual elapsed time. */
static LONGLONG gs_snapshot_100ns_locked(void)
{
    return g_anchorFt100 + gs_elapsed_100ns_locked();
}

static int gs_factor_valid(double factor)
{
    /* !(f == f) rejects NaN; the range test rejects both infinities. */
    if (!(factor == factor)) return 0;
    if (factor < GS_MIN_FACTOR || factor > GS_MAX_FACTOR) return 0;
    return 1;
}

static LONGLONG gs_factor_to_fp(double factor)
{
    if (factor == 1.0) return GS_FP_ONE;   /* exact, never round */
    return (LONGLONG)(factor * (double)GS_FP_ONE + (factor < 0 ? -0.5 : 0.5));
}

static double gs_factor_from_fp(LONGLONG fp)
{
    return (double)fp / (double)GS_FP_ONE;
}

static LONGLONG gs_factor_micro(LONGLONG fp)
{
    return (LONGLONG)(gs_factor_from_fp(fp) * (double)GS_FACTOR_SCALE + 0.5);
}

/* Adopts a new factor with NO time jump. Callers hold the lock. */
static void gs_apply_factor_locked(LONGLONG newFp)
{
    if (newFp == g_factorFp) return;

    LONGLONG now = gs_now_real_qpc();
    if (now > 0)
    {
        /*
         * Always integrate; never re-seed the accumulator from the anchor.
         *
         * Integrating at exactly 1.0 is the identity (delta * GS_FP_ONE == delta),
         * so the normal path costs nothing extra and needs no special case. The
         * re-seed this replaces looked like a free optimisation but silently threw
         * away every microsecond of accumulated scaled time, so leaving 1.0x
         * snapped the virtual clock back onto the real clock. The observable effect
         * was that a speed factor never actually applied: every API kept reporting
         * 1.0x worth of elapsed time no matter what the factor was set to.
         *
         * It is also required for continuity: a game that samples the clock across a
         * factor change must never see time jump, and a game that computes frame
         * deltas must never see a negative one.
         */
        gs_integrate(now);
        /* Re-base to this instant, so only the interval after the change is
           charged at the new rate. */
        g_realLastQpc = now;
    }
    g_factorFp = newFp;
    InterlockedExchange(&g_scaled, newFp == GS_FP_ONE ? 0 : 1);
}

static void gs_force_normal_locked(void)
{
    g_factorFp = GS_FP_ONE;
    InterlockedExchange(&g_scaled, 0);
}

/* ------------------------------------------------------------- the hooks */

static DWORD WINAPI gs_hook_GetTickCount(void)
{
    LONGLONG t;
    if (InterlockedCompareExchange((volatile LONG *)&g_scaled, 0, 0) == 0)
    {
        if (g_realTick32 != NULL) return g_realTick32();
        return (DWORD)(gs_now_real_qpc() / (g_freq / 1000));
    }
    gs_enter();
    t = gs_elapsed_100ns_locked();
    gs_leave();
    return (DWORD)(g_tickBase32 + (ULONGLONG)(t / 10000));
}

static ULONGLONG WINAPI gs_hook_GetTickCount64(void)
{
    LONGLONG t;
    if (InterlockedCompareExchange((volatile LONG *)&g_scaled, 0, 0) == 0)
    {
        if (g_realTick64 != NULL) return g_realTick64();
        return (ULONGLONG)(gs_now_real_qpc() / (g_freq / 10));
    }
    gs_enter();
    t = gs_elapsed_100ns_locked();
    gs_leave();
#if defined(GS_DIAG)
    gs_trace("HOOK tick64 base=%llu elapsed100ns=%lld freq=%lld virtual=%lld -> %llu",
             (unsigned long long)g_tickBase64, (long long)t, (long long)g_freq,
             (long long)g_virtualFp, (unsigned long long)(g_tickBase64 + (ULONGLONG)(t / 10)));
#endif
    return g_tickBase64 + (ULONGLONG)(t / 10);
}

static BOOL WINAPI gs_hook_QueryPerformanceCounter(LARGE_INTEGER *out)
{
    if (out == NULL) return FALSE;
    if (InterlockedCompareExchange((volatile LONG *)&g_scaled, 0, 0) == 0)
    {
        /* Exact pass-through: hand back the real counter value untouched. */
        return (g_realQpc != NULL) ? g_realQpc(out) : FALSE;
    }
    gs_enter();
    out->QuadPart = gs_snapshot_qpc_locked();
    gs_leave();
#if defined(GS_DIAG)
    gs_trace("HOOK qpc scaled=%ld factorFp=%lld virtual=%lld -> %lld",
             (long)g_scaled, (long long)g_factorFp, (long long)g_virtualFp, (long long)out->QuadPart);
#endif
    return TRUE;
}

static DWORD WINAPI gs_hook_timeGetTime(void)
{
    LONGLONG t;
    if (g_realMidi == NULL) return 0;
    if (InterlockedCompareExchange((volatile LONG *)&g_scaled, 0, 0) == 0)
        return g_realMidi();
    gs_enter();
    t = gs_elapsed_100ns_locked();
    gs_leave();
    return (DWORD)(g_midiBase + (ULONGLONG)(t / 10000));
}

static void gs_fill_filetime(LPFILETIME ft, LONGLONG hundredNanos)
{
    ULONGLONG v;
    if (ft == NULL) return;
    v = (ULONGLONG)hundredNanos;
    ft->dwLowDateTime  = (DWORD)(v & 0xFFFFFFFFull);
    ft->dwHighDateTime = (DWORD)((v >> 32) & 0xFFFFFFFFull);
}

static void WINAPI gs_hook_GetSystemTimeAsFileTime(LPFILETIME ft)
{
    LONGLONG t;
    if (InterlockedCompareExchange((volatile LONG *)&g_scaled, 0, 0) == 0)
    {
        /* At 1.0 the real date and time of day are passed straight through. */
        if (g_realFt != NULL) g_realFt(ft);
        else if (g_realNt != NULL) g_realNt(ft);
        return;
    }
    gs_enter();
    t = gs_snapshot_100ns_locked();
    gs_leave();
    gs_fill_filetime(ft, t);
}

/* ---------------------------------------------------------- x86-64 decoder */

/*
 * Instruction-length and relocation decoding for the classes that actually appear
 * in Win32 API prologues. Anything unrecognised makes gs_ilen return 0, and the
 * installer then skips that hook. Failing to patch is always safe; mis-decoding an
 * instruction and jumping into the middle of it is not.
 */

static int gs_modrm(const BYTE *p, int hasRexB, int hasRexX, int *rel32)
{
    BYTE m = *p;
    int mod = (m >> 6) & 3;
    int rm  = m & 7;

    if (rel32 != NULL) *rel32 = 0;
    if (mod == 3) return 1;                                   /* register operand */

    if (hasRexX) rm |= 8;
    if (hasRexB) rm |= 8;

    if (rm == 4)
    {
        /* r/m=100 means a SIB byte follows. The SIB base field decides the
           displacement: base=101 with mod=00 still takes a disp32, because there
           is no base register to hold the address. REX.B turns that r13 back into a
           real base. */
        int base = p[1] & 7;
        int noBase = (mod == 0 && base == 5);
#if defined(_WIN64)
        noBase = noBase && !hasRexB;
#endif
        /* A SIB disp32 is an absolute address in both modes, so it never needs
           re-basing; only the plain mod=00 r/m=101 form is RIP-relative. */
        if (noBase) return 6;                                 /* modrm + sib + disp32 */
        if (mod == 0) return 2;                               /* modrm + sib */
        if (mod == 1) return 3;                               /* modrm + sib + disp8 */
        return 6;                                             /* modrm + sib + disp32 */
    }

    if (mod == 0)
    {
        if (rm == 5)
        {
            /* mod=00 r/m=101 is disp32: RIP-relative under 64-bit mode, an absolute
               address under 32-bit mode where it must be left alone. */
            if (rel32 != NULL)
            {
#if defined(_WIN64)
                *rel32 = 1;
#else
                *rel32 = 0;
#endif
            }
            return 6;
        }
        return 1;                                             /* [rax], [rcx], [r8], ... */
    }
    if (mod == 1) return 3;                                   /* modrm + disp8 */
    return 6;                                                 /* modrm + disp32 */
}

static int gs_has_modrm(BYTE b)
{
    /* b is a BYTE, so b <= 0x03 already means 0x00..0x03. */
    if (b <= 0x03 || (b >= 0x08 && b <= 0x0B) ||
        (b >= 0x10 && b <= 0x13) || (b >= 0x18 && b <= 0x1B) ||
        (b >= 0x20 && b <= 0x23) || (b >= 0x28 && b <= 0x2B) ||
        (b >= 0x30 && b <= 0x33) || (b >= 0x38 && b <= 0x3B)) return 1;
    switch (b)
    {
        case 0x62: case 0x63: case 0x69: case 0x6B:
        case 0x80: case 0x81: case 0x82: case 0x83:
        case 0x84: case 0x85: case 0x86: case 0x87:
        case 0x88: case 0x89: case 0x8A: case 0x8B:
        case 0x8C: case 0x8D: case 0x8E: case 0x8F:
        case 0xC0: case 0xC1: case 0xC6: case 0xC7:
        case 0xD0: case 0xD1: case 0xD2: case 0xD3:
        case 0xF6: case 0xF7: case 0xFE: case 0xFF:
            return 1;
    }
    return 0;
}

static int gs_0f_has_modrm(BYTE b)
{
    if (b >= 0x80 && b <= 0x8F) return 0;                 /* jcc rel32       */
    if (b == 0x05 || b == 0x06 || b == 0x07 || b == 0x08 || b == 0x09) return 0;
    if (b == 0x0B || b == 0x0E) return 0;                 /* ud2, femms      */
    if (b >= 0x30 && b <= 0x37) return (b == 0x38) ? 0 : (b >= 0x35 ? 0 : 1);
    if (b >= 0x70 && b <= 0x7F) return ((b == 0x7E) || (b == 0x7F)) ? 1 : 0;
    if (b >= 0xA0 && b <= 0xAF) return 0;
    if (b == 0x38 || b == 0x3A) return 0;                  /* three-byte escapes */
    if (b == 0xC8 || b == 0xC9 || b == 0xCA || b == 0xCB ||
        b == 0xCC || b == 0xCD || b == 0xCE || b == 0xCF) return 0;
    return 1;
}

static int gs_0f_len(const BYTE *p, int opsz, int *rel)
{
    BYTE b = p[1];
    int hasRexB = 0, hasRexX = 0;
    int i;

    for (i = 1; i <= 5; i++)              /* the last REX before the 0x0F */
    {
        BYTE q = p[-i];
        if (q >= 0x40 && q <= 0x4F) { hasRexB = (q & 1) != 0; hasRexX = (q & 2) != 0; break; }
    }

    if (b >= 0x80 && b <= 0x8F) { if (rel != NULL) *rel = 2; return 6; }
    if (b == 0x0F)                                   /* 3DNow!: modrm + imm8 */
    {
        int m;
        if (gs_modrm(p + 2, hasRexB, hasRexX, NULL) == 0) return 0;
        m = gs_modrm(p + 2, hasRexB, hasRexX, NULL);
        return 2 + m + 1;
    }
    if (!gs_0f_has_modrm(b))
    {
        if (b >= 0x90 && b <= 0x9F) return 3;                    /* setcc r/m8     */
        if (b == 0xA0 || b == 0xA1 || b == 0xA8 || b == 0xA9) return 2;
        if (b == 0xC8 || b == 0xC9) return (opsz == 2) ? 4 : 6;  /* bswap, lddqu   */
        if (b >= 0x70 && b <= 0x7F) return 4;                    /* pshufw imm8    */
        return 2;
    }
    {
        int mrel = 0;
        int m = gs_modrm(p + 2, hasRexB, hasRexX, &mrel);
        if (m == 0) return 0;
        if (mrel && rel != NULL) *rel = 2;
        return 2 + m;
    }
}

static int gs_ilen(const BYTE *p, int *rel)
{
    int n = 0, depth = 0, opsz = 4;
    int hasRexB = 0, hasRexX = 0;
    BYTE b;

    if (rel != NULL) *rel = -2;
    for (;;)
    {
        b = p[n];
        if (b == 0x66) { opsz = 2; }
        else if (b == 0x67 || b == 0xF0 || b == 0xF2 || b == 0xF3 ||
                 b == 0x2E || b == 0x36 || b == 0x3E || b == 0x26 ||
                 b == 0x64 || b == 0x65)
        {
            /* legal prefix, no length effect */
        }
#if defined(_WIN64)
        else if (b >= 0x40 && b <= 0x4F) { hasRexB = (b & 1) != 0; hasRexX = (b & 2) != 0; }
#else
        else if (b == 0x40 || b == 0x41) { n++; depth++; if (depth > 5) return 0; continue; }
#endif
        else break;
        n++; depth++;
        if (depth > 5) return 0;
    }

    b = p[n];

    if (b == 0x0F)
    {
        int sub = -2;
        int len = gs_0f_len(p + n, opsz, &sub);
        if (len <= 0) return 0;
        if (rel != NULL && sub >= 0) *rel = n + sub;
        return n + len;
    }

    if (b == 0xE8 || b == 0xE9) { if (rel != NULL) *rel = n + 1; return n + 5; }
    if (b == 0xEB)              { if (rel != NULL) *rel = n + 1; return n + 2; }
    if (b >= 0x70 && b <= 0x7F) { if (rel != NULL) *rel = n + 1; return n + 2; }
    if (b >= 0xB0 && b <= 0xB7) return n + 2;
    if (b >= 0xB8 && b <= 0xBF) return n + ((opsz == 2) ? 3 : 5);
    if (b == 0xC2) return n + 3;                        /* ret imm16          */
    if (b == 0xC8) return n + 4;                        /* enter imm16, imm8  */
    if (b == 0x6A) return n + 2;                        /* push imm8          */
    if (b == 0x68) return n + 5;                        /* push imm32         */
    if (b == 0xA8) return n + 2;                        /* test al, imm8      */
    if (b == 0xA9) return n + ((opsz == 2) ? 3 : 5);

    if (gs_has_modrm(b))
    {
        int mrel = 0, extra = 0, m;
        BYTE reg = (BYTE)((p[n + 1] >> 3) & 7);
        m = gs_modrm(p + n + 1, hasRexB, hasRexX, &mrel);
        if (m == 0) return 0;
        if (mrel && rel != NULL) *rel = n + 2;
        if (b == 0x80 || b == 0x82 || b == 0x83 || b == 0xC0 ||
            b == 0xC1 || b == 0xC6 || b == 0x6B) extra = 1;
        else if (b == 0x81 || b == 0xC7 || b == 0x69) extra = (opsz == 2) ? 2 : 4;
        else if (b == 0xF6 && reg <= 1) extra = 1;
        else if (b == 0xF7 && reg <= 1) extra = (opsz == 2) ? 2 : 4;
        return n + 1 + m + extra;
    }
    return n + 1;
}

/* ---------------------------------------------------------- detour engine */

typedef struct GS_HOOK
{
    void *target;
    void *detour;
    void *trampoline;
    BYTE  stolen[32];
    int   stolenLen;
    int   installed;
} GS_HOOK;

static GS_HOOK g_hooks[16];
static int     g_hookCount;
static PVOID   g_veh = NULL;

static int gs_rel32_ok(const void *from, const void *to)
{
    LONGLONG d = (LONGLONG)((const char *)to - (const char *)from);
    return (d >= -0x7FFFFFFFLL) && (d <= 0x7FFFFFFFLL);
}

static size_t gs_build_jump(const void *from, const void *to, BYTE *buf, size_t cap)
{
    if (gs_rel32_ok(from, to) && cap >= 5)
    {
        LONGLONG rel = (LONGLONG)((const char *)to - ((const char *)from + 5));
        buf[0] = 0xE9;                                   /* jmp rel32 */
        memcpy(buf + 1, &rel, 4);
        return 5;
    }
#if defined(_WIN64)
    if (cap >= 14)
    {
        ULONGLONG addr = (ULONGLONG)(ULONG_PTR)to;
        memset(buf, 0, 14);
        buf[0] = 0xFF; buf[1] = 0x25;                     /* jmp qword ptr [rip+0] */
        memcpy(buf + 6, &addr, 8);
        return 14;
    }
#else
    if (cap >= 6)
    {
        unsigned long addr = (unsigned long)(ULONG_PTR)to;
        buf[0] = 0xFF; buf[1] = 0x25;                     /* jmp dword ptr [addr] */
        memcpy(buf + 2, &addr, 4);
        return 6;
    }
#endif
    return 0;
}

/* Allocates the detour inside the +/-2 GB rel32 reach of the target so the cheap
   5-byte jump can be used. Address space is sparse enough that a hint address
   essentially always lands. */
static void *gs_alloc_near(void *target, size_t size)
{
    const ULONG_PTR step = 0x10000UL;
    const ULONG_PTR span = 0x7F000000UL;
    ULONG_PTR base = (ULONG_PTR)target;
    int i;

    for (i = 1; i <= 2048; i++)
    {
        ULONG_PTR cand = 0;
        ULONG_PTR up = base + (ULONG_PTR)i * step;
        if (up > base && up < base + span) cand = up;
        if (cand == 0)
        {
            ULONG_PTR dn = base - (ULONG_PTR)i * step;
            if (dn > 0x10000UL + size) cand = dn;
        }
        if (cand == 0) break;
        {
            void *p = VirtualAlloc((void *)cand, size, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
            if (p != NULL)
            {
                if (gs_rel32_ok((void *)base, p)) return p;
                VirtualFree(p, 0, MEM_RELEASE);
            }
        }
    }
    return NULL;
}

static int gs_write_code(void *addr, const BYTE *data, size_t len)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(addr, len, PAGE_EXECUTE_READWRITE, &oldProtect)) return 0;
    memcpy(addr, data, len);
    FlushInstructionCache(GetCurrentProcess(), addr, len);
    {
        DWORD ignored = 0;
        VirtualProtect(addr, len, oldProtect, &ignored);
    }
    return 1;
}

static void gs_restore_all(void)
{
    int i;
    for (i = 0; i < g_hookCount; i++)
    {
        GS_HOOK *h = &g_hooks[i];
        if (!h->installed) continue;
        gs_write_code(h->target, h->stolen, (size_t)h->stolenLen);
        h->installed = 0;
    }
    g_hookCount = 0;
    if (g_block != NULL)
    {
        g_block->flags &= ~(unsigned __int64)GS_FLAG_HOOKS;
        g_block->flags |= GS_FLAG_FAULTED;
        g_block->hookCount = 0;
    }
}

/*
 * A fault inside a patched prologue is the one way a detour can take the host
 * down. Restoring the ORIGINAL bytes is the only safe repair: a thread already
 * inside a hook simply finishes and returns, and a thread about to enter the
 * function executes the untouched prologue.
 */
static LONG CALLBACK gs_veh(PEXCEPTION_POINTERS ep)
{
    DWORD code;

    if (ep == NULL || ep->ExceptionRecord == NULL) return EXCEPTION_CONTINUE_SEARCH;
    code = ep->ExceptionRecord->ExceptionCode;
    if (code != EXCEPTION_ACCESS_VIOLATION &&
        code != EXCEPTION_ILLEGAL_INSTRUCTION &&
        code != EXCEPTION_GUARD_PAGE &&
        code != EXCEPTION_PRIV_INSTRUCTION)
        return EXCEPTION_CONTINUE_SEARCH;

    {
        const BYTE *rip = NULL;
        int i;
#if defined(_WIN64)
        if (ep->ContextRecord != NULL) rip = (const BYTE *)(ULONG_PTR)ep->ContextRecord->Rip;
#else
        if (ep->ContextRecord != NULL) rip = (const BYTE *)(ULONG_PTR)ep->ContextRecord->Eip;
#endif
        if (rip == NULL) return EXCEPTION_CONTINUE_SEARCH;
        for (i = 0; i < g_hookCount; i++)
        {
            GS_HOOK *h = &g_hooks[i];
            if (h->installed && rip >= (const BYTE *)h->target &&
                rip < (const BYTE *)h->target + h->stolenLen)
            {
                gs_restore_all();
                return EXCEPTION_CONTINUE_EXECUTION;
            }
        }
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

static void *gs_resolve(HMODULE module, const char *name)
{
    return (module != NULL) ? (void *)GetProcAddress(module, name) : NULL;
}

/*
 * Installs one detour. Returns 1 on success. Every failure path leaves the target
 * function byte-for-byte untouched, so a refused hook can never crash the game,
 * and the caller simply proceeds to the next API.
 *
 * The amount of code stolen is decided from the jump form actually used, so the
 * trampoline and the patch always displace the same number of bytes.
 */
static int gs_install(void *target, void *detour)
{
    BYTE  relOff[16], relWide[16];
    int   need, len, nrel = 0, i;
    BYTE  patch[16];
    void *detourMem;
    size_t patchLen;

    if (target == NULL || detour == NULL) return 0;
    if ((const BYTE *)target < (const BYTE *)0x10000) return 0;   /* a stub, not code */

    for (i = 0; i < g_hookCount; i++)
        if (g_hooks[i].target == target) return 1;                 /* idempotent */
    if (g_hookCount >= (int)(sizeof(g_hooks) / sizeof(g_hooks[0]))) return 0;

    /* A nearby allocation lets the 5-byte rel32 jump cover a 5-byte steal. */
    detourMem = gs_alloc_near(target, sizeof(patch) + 40);
    if (detourMem != NULL)
    {
        need = 5;
    }
    else
    {
        detourMem = VirtualAlloc(NULL, sizeof(patch) + 40, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (detourMem == NULL) return 0;
#if defined(_WIN64)
        need = 14;
#else
        need = 6;
#endif
    }

    len = 0;
    while (len < need && len < 24)
    {
        int rel = -2;
        int ilen = gs_ilen((const BYTE *)target + len, &rel);
        if (ilen <= 0 || len + ilen > 24) { VirtualFree(detourMem, 0, MEM_RELEASE); return 0; }
        if (rel >= 0)
        {
            if (nrel >= 16) { VirtualFree(detourMem, 0, MEM_RELEASE); return 0; }
            relOff[nrel]  = (BYTE)(len + rel);
            relWide[nrel] = (BYTE)(ilen == rel + 1 ? 1 : 4);
            nrel++;
        }
        len += ilen;
    }
    if (len < need) { VirtualFree(detourMem, 0, MEM_RELEASE); return 0; }

    {
        GS_HOOK *h = &g_hooks[g_hookCount];
        BYTE *tramp = (BYTE *)detourMem;
        /*
         * RIP-relative displacement fixup. For an instruction of length L copied to
         * the trampoline:
         *     (tramp + L) + new_disp == (target + L) + old_disp
         * so new_disp == old_disp + (target - tramp), NOT (tramp - target). Using
         * the wrong sign sends every relocated jump exactly 2*delta off its original
         * destination, which lands in unmapped memory and kills the host process.
         */
        LONGLONG delta = (LONGLONG)(ULONG_PTR)target - (LONGLONG)(ULONG_PTR)tramp;

        memset(h, 0, sizeof(*h));
        h->target = target;
        h->detour = detour;
        h->trampoline = detourMem;
        h->stolenLen = len;
        memcpy(h->stolen, target, (size_t)len);

        /* trampoline = relocated stolen instructions + jump back to target+len */
        memcpy(tramp, h->stolen, (size_t)len);
        for (i = 0; i < nrel; i++)
        {
            BYTE *f = tramp + relOff[i];
            if (relWide[i] == 4)
            {
                LONG v = 0;
                memcpy(&v, f, 4);
                v = (LONG)((LONGLONG)v + delta);
                memcpy(f, &v, 4);
            }
            else
            {
                signed char v = 0;
                memcpy(&v, f, 1);
                v = (signed char)((LONGLONG)v + delta);
                memcpy(f, &v, 1);
            }
        }
        if (gs_build_jump(tramp + len, (const BYTE *)target + len, tramp + len, (size_t)40) == 0)
        {
            VirtualFree(detourMem, 0, MEM_RELEASE);
            return 0;
        }

        /* The entry point must redirect to the DETOUR, not to the trampoline. Jumping to
           the trampoline here would re-enter the original instructions and make the
           whole patch a silent no-op: nothing would crash and nothing would scale. */
        patchLen = gs_build_jump(target, detour, patch, sizeof(patch));
        if (patchLen == 0) { VirtualFree(detourMem, 0, MEM_RELEASE); return 0; }

        GS_TRACE("gs_install %p len=%d patchLen=%zu tramp=%p nrel=%d",
                 target, len, patchLen, tramp, nrel);
        if (!gs_write_code(target, patch, patchLen))
        {
            GS_TRACE("gs_install %p VirtualProtect/write FAILED err=%lu", target, GetLastError());
            VirtualFree(detourMem, 0, MEM_RELEASE);
            return 0;
        }
        h->installed = 1;
        g_hookCount++;
        return 1;
    }
}

static HMODULE gs_module(const wchar_t *name)
{
    return GetModuleHandleW(name);
}

static void gs_resolve_originals(void)
{
    HMODULE k32 = gs_module(L"kernel32.dll");
    HMODULE kbase = gs_module(L"kernelbase.dll");
    HMODULE ntdll = gs_module(L"ntdll.dll");
    HMODULE winmm = gs_module(L"winmm.dll");

    g_realQpc = (GS_QpcFn)gs_resolve(k32, "QueryPerformanceCounter");
    if (g_realQpc == NULL) g_realQpc = (GS_QpcFn)gs_resolve(kbase, "QueryPerformanceCounter");
    if (g_realQpc == NULL) g_realQpc = (GS_QpcFn)gs_resolve(ntdll, "NtQueryPerformanceCounter");

    /* The scale of the counter, NOT another counter reading: every conversion from
       virtual QPC counts to 100ns units divides by this. */
    g_realFreq = (GS_FrequencyFn)gs_resolve(k32, "QueryPerformanceFrequency");
    if (g_realFreq == NULL) g_realFreq = (GS_FrequencyFn)gs_resolve(kbase, "QueryPerformanceFrequency");

    g_realTick64 = (GS_GetTickCount64Fn)gs_resolve(k32, "GetTickCount64");
    g_realTick32 = (GS_GetTickCountFn)gs_resolve(k32, "GetTickCount");
    g_realFt     = (GS_FileTimeFn)gs_resolve(k32, "GetSystemTimeAsFileTime");
    g_realNt     = (GS_FileTimeFn)gs_resolve(ntdll, "NtQuerySystemTime");
    if (winmm != NULL) g_realMidi = (GS_TimeGetTimeFn)gs_resolve(winmm, "timeGetTime");

    {
        LARGE_INTEGER f;
        if (g_realFreq != NULL && g_realFreq(&f) && f.QuadPart > 0) g_freq = f.QuadPart;
    }
    if (g_freq <= 0) g_freq = 10000000LL;   /* never divide by zero */
}

/* Every clock domain's base is sampled once, before any patch is written. */
static void gs_seed_clock(void)
{
    LARGE_INTEGER c;
    FILETIME ft;

    if (g_realQpc != NULL && g_realQpc(&c)) g_anchorQpc = c.QuadPart;
    g_realLastQpc = g_anchorQpc;
    g_virtualFp = 0;
    if (g_realFt != NULL)
    {
        g_realFt(&ft);
        g_anchorFt100 = ((LONGLONG)(LONGLONG)ft.dwHighDateTime << 32) | (LONGLONG)ft.dwLowDateTime;
    }
    if (g_realTick64 != NULL) g_tickBase64 = g_realTick64();
    if (g_realTick32 != NULL) g_tickBase32 = g_realTick32();
    if (g_realMidi != NULL)   g_midiBase   = g_realMidi();
}

/*
 * Returns the address that actually reaches the real implementation.
 *
 * CRITICAL: a detour overwrites the first bytes of the function it patches, so a
 * pointer to that address is no longer a way to reach the original code - calling it
 * re-enters the detour and recurses until the stack is gone. Every saved "original"
 * pointer must therefore be redirected to this hook's trampoline, which holds the
 * relocated stolen instructions followed by a jump back into the untouched body.
 */
static void *gs_trampoline_for(void *address)
{
    int i;
    if (address == NULL) return NULL;
    for (i = 0; i < g_hookCount; i++)
        if (g_hooks[i].installed && g_hooks[i].target == address) return g_hooks[i].trampoline;
    return address;
}

static void gs_install_hooks(void)
{
    HMODULE k32 = gs_module(L"kernel32.dll");
    HMODULE kbase = gs_module(L"kernelbase.dll");
    HMODULE ntdll = gs_module(L"ntdll.dll");
    HMODULE winmm = gs_module(L"winmm.dll");

    /*
     * Each API is patched at ONE site only: the kernel32 export, falling back to
     * kernelbase only when the kernel32 patch could not be written.
     *
     * Patching both is a hard error, not a belt-and-braces improvement. On this
     * class of Windows kernel32!QueryPerformanceCounter is a RIP-relative forwarder
     * that tail-jumps into kernelbase. If both entry points are detoured, the
     * trampoline's relocated `jmp` lands on kernelbase - which is now the detour
     * again - so the trampoline calls the hook that called the trampoline, forever,
     * and the host process dies of a stack overflow. One site per function keeps
     * every trampoline terminating in untouched code.
     */
    GS_TRACE("gs_install_hooks k32=%p kbase=%p", (void *)k32, (void *)kbase);
    GS_TRACE("  realQpc=%p realTick32=%p realTick64=%p realFt=%p realNt=%p freq=%lld",
             (void *)g_realQpc, (void *)g_realTick32, (void *)g_realTick64,
             (void *)g_realFt, (void *)g_realNt, (long long)g_freq);
    if (!gs_install(gs_resolve(k32, "GetTickCount"), (void *)gs_hook_GetTickCount))
        gs_install(gs_resolve(kbase, "GetTickCount"), (void *)gs_hook_GetTickCount);
    if (!gs_install(gs_resolve(k32, "GetTickCount64"), (void *)gs_hook_GetTickCount64))
        gs_install(gs_resolve(kbase, "GetTickCount64"), (void *)gs_hook_GetTickCount64);
    if (!gs_install(gs_resolve(k32, "QueryPerformanceCounter"), (void *)gs_hook_QueryPerformanceCounter))
        gs_install(gs_resolve(kbase, "QueryPerformanceCounter"), (void *)gs_hook_QueryPerformanceCounter);
    if (!gs_install(gs_resolve(k32, "GetSystemTimeAsFileTime"), (void *)gs_hook_GetSystemTimeAsFileTime))
        gs_install(gs_resolve(kbase, "GetSystemTimeAsFileTime"), (void *)gs_hook_GetSystemTimeAsFileTime);
    if (!gs_install(gs_resolve(ntdll, "NtQuerySystemTime"), (void *)gs_hook_GetSystemTimeAsFileTime))
        gs_install(gs_resolve(k32, "NtQuerySystemTime"), (void *)gs_hook_GetSystemTimeAsFileTime);
    gs_install(gs_resolve(winmm, "timeGetTime"), (void *)gs_hook_timeGetTime);

    /* Redirect every saved original through its trampoline. Without this the hooks
       would recurse into themselves the first time they were called. */
    if (g_realQpc != NULL)   g_realQpc   = (GS_QpcFn)gs_trampoline_for((void *)g_realQpc);
    if (g_realTick64 != NULL) g_realTick64 = (GS_GetTickCount64Fn)gs_trampoline_for((void *)g_realTick64);
    if (g_realTick32 != NULL) g_realTick32 = (GS_GetTickCountFn)gs_trampoline_for((void *)g_realTick32);
    if (g_realMidi != NULL)  g_realMidi  = (GS_TimeGetTimeFn)gs_trampoline_for((void *)g_realMidi);
    if (g_realFt != NULL)    g_realFt    = (GS_FileTimeFn)gs_trampoline_for((void *)g_realFt);
    if (g_realNt != NULL)    g_realNt    = (GS_FileTimeFn)gs_trampoline_for((void *)g_realNt);
    GS_TRACE("  after redirect: qpc=%p tick32=%p tick64=%p ft=%p hooks=%d",
             (void *)g_realQpc, (void *)g_realTick32, (void *)g_realTick64, (void *)g_realFt, g_hookCount);
#if defined(GS_DIAG)
    {
        int z;
        for (z = 0; z < g_hookCount; z++)
        {
            const unsigned char *t = (const unsigned char *)g_hooks[z].target;
            char hex[40];
            {   int b; for (b = 0; b < 8; b++) sprintf(hex + b * 2, "%02X", t[b]); }
            GS_TRACE("  verify %p now: %s (was %02X %02X %02X %02X %02X)",
                     (const void *)t, hex,
                     g_hooks[z].stolen[0], g_hooks[z].stolen[1], g_hooks[z].stolen[2],
                     g_hooks[z].stolen[3], g_hooks[z].stolen[4]);
        }
    }
#endif
}

/* -------------------------------------------------------- control block */

static void gs_u64_to_dec(ULONGLONG value, wchar_t *out)
{
    wchar_t tmp[24];
    int n = 0, i;
    if (value == 0) { out[0] = L'0'; out[1] = 0; return; }
    while (value > 0 && n < 24) { tmp[n++] = (wchar_t)(L'0' + (wchar_t)(value % 10)); value /= 10; }
    for (i = 0; i < n; i++) out[i] = tmp[n - 1 - i];
    out[n] = 0;
}

static void gs_control_name(wchar_t *out)
{
    wchar_t digits[24];
    const wchar_t prefix[] = L"Local\\GameLibrary-GameSpeed-";
    size_t i = 0, p = 0;
    while (prefix[p] != 0) out[i++] = prefix[p++];
    gs_u64_to_dec((ULONGLONG)GetCurrentProcessId(), digits);
    p = 0;
    while (digits[p] != 0) out[i++] = digits[p++];
    out[i] = 0;
}

static void gs_block_publish(void)
{
    if (g_block == NULL) return;
    g_block->flags |= GS_FLAG_BLOCK;
    g_block->hookCount = (unsigned __int64)(LONGLONG)g_hookCount;
    g_block->anchorQpc = (unsigned __int64)(LONGLONG)g_anchorQpc;
    g_block->qpcFrequency = (unsigned __int64)(LONGLONG)g_freq;
    if (g_hookCount > 0) g_block->flags |= GS_FLAG_HOOKS;
}

static void gs_block_attach(void)
{
    wchar_t name[64];

    gs_control_name(name);
    /* Create the mapping when absent so a manager that attaches late, or that
       restarts after a crash, always finds the block this DLL is honouring. */
    g_blockMap = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (g_blockMap == NULL)
        g_blockMap = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE,
                                        0, (DWORD)GS_BLOCK_BYTES, name);
    if (g_blockMap == NULL) return;
    g_block = (GS_BLOCK *)MapViewOfFile(g_blockMap, FILE_MAP_ALL_ACCESS, 0, 0, GS_BLOCK_BYTES);
    if (g_block == NULL)
    {
        CloseHandle(g_blockMap);
        g_blockMap = NULL;
        return;
    }
    gs_block_publish();
}

/* ---------------------------------------------------------- watchdog loop */

static void gs_watchdog_pass(void)
{
    LONGLONG now, beat, desired, generation, elapsedMs;

    if (g_block == NULL) return;
    if (InterlockedCompareExchange(&g_shuttingDown, 0, 0) != 0) return;

    now = gs_now_real_qpc();
    if (now <= 0) return;

    gs_enter();

    beat = (LONGLONG)g_block->ownerHeartbeat;
    if (beat != g_lastBeat)
    {
        g_lastBeat = beat;
        g_beatRealQpc = now;
        if ((g_block->flags & (unsigned __int64)GS_FLAG_DETACHED) != 0)
            g_block->flags &= ~(unsigned __int64)GS_FLAG_DETACHED;
    }

    desired = (LONGLONG)g_block->desiredFactorMicro;
    generation = (LONGLONG)g_block->generation;

    if ((g_block->flags & (unsigned __int64)GS_FLAG_DETACHED) == 0 &&
        generation != (LONGLONG)g_block->activeGeneration)
    {
        double f = (desired > 0) ? ((double)desired / (double)GS_FACTOR_SCALE) : 1.0;
        if (!gs_factor_valid(f)) f = 1.0;
        gs_apply_factor_locked(gs_factor_to_fp(f));
        g_block->activeGeneration = generation;
        g_block->appliedFactorMicro = (unsigned __int64)(LONGLONG)gs_factor_micro(g_factorFp);
        g_block->virtualElapsedQpc = (unsigned __int64)(LONGLONG)(g_virtualFp >> GS_FP_BITS);
        g_beatRealQpc = now;
        gs_leave();
        return;
    }

    /* The manager stopped steering us, or went silent: hand the game its real
       time back. This is what makes "the app exited" safe even when the app
       exited by being killed. */
    if (g_factorFp != GS_FP_ONE &&
        (((g_block->flags & (unsigned __int64)GS_FLAG_DETACHED) != 0) ||
         (g_beatRealQpc != 0 &&
          (elapsedMs = (now - g_beatRealQpc) * 1000 / g_freq) > GS_HEARTBEAT_TIMEOUT_MS)))
    {
        gs_apply_factor_locked(GS_FP_ONE);
        g_block->appliedFactorMicro = GS_FACTOR_SCALE;
        g_block->flags |= GS_FLAG_FAULTED;
        g_beatRealQpc = now;
    }

    g_block->virtualElapsedQpc = (unsigned __int64)(LONGLONG)(g_virtualFp >> GS_FP_BITS);
    gs_leave();
}

static DWORD WINAPI gs_watchdog(LPVOID unused)
{
    (void)unused;
    for (;;)
    {
        if (WaitForSingleObject(g_stopEvent, GS_WATCHDOG_TICK_MS) == WAIT_OBJECT_0) break;
        gs_watchdog_pass();
    }
    return 0;
}

/* ------------------------------------------------------------- DllMain */

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        InitializeCriticalSection(&g_lock);
        InterlockedExchange(&g_lockReady, 1);

        gs_resolve_originals();
        gs_seed_clock();
        gs_block_attach();
        g_veh = AddVectoredExceptionHandler(1, gs_veh);
        gs_install_hooks();
        gs_block_publish();

        g_stopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
        if (g_stopEvent != NULL)
            g_watchThread = CreateThread(NULL, 0, gs_watchdog, NULL, 0, NULL);
        return TRUE;
    }

    if (reason == DLL_PROCESS_DETACH)
    {
        InterlockedExchange(&g_shuttingDown, 1);
        if (g_stopEvent != NULL) SetEvent(g_stopEvent);
        if (g_watchThread != NULL)
        {
            WaitForSingleObject(g_watchThread, 2000);
            CloseHandle(g_watchThread);
            g_watchThread = NULL;
        }
        if (g_stopEvent != NULL) { CloseHandle(g_stopEvent); g_stopEvent = NULL; }
        gs_enter();
        gs_force_normal_locked();
        gs_leave();
        if (g_veh != NULL) { RemoveVectoredExceptionHandler(g_veh); g_veh = NULL; }
        if (g_block != NULL) { UnmapViewOfFile(g_block); g_block = NULL; }
        if (g_blockMap != NULL) { CloseHandle(g_blockMap); g_blockMap = NULL; }
        DeleteCriticalSection(&g_lock);
        return TRUE;
    }
    return TRUE;
}

/* -------------------------------------------------------------- exports */

int __cdecl GameSpeed_Set(double factor)
{
    if (!gs_factor_valid(factor)) return 0;
    GS_TRACE("GameSpeed_Set(%f)", factor);
    gs_enter();
    gs_apply_factor_locked(gs_factor_to_fp(factor));
    GS_TRACE("  applied factorFp=%lld scaled=%ld", (long long)g_factorFp, (long)g_scaled);
    if (g_block != NULL)
    {
        g_block->activeGeneration = g_block->generation;
        g_block->appliedFactorMicro = (unsigned __int64)(LONGLONG)gs_factor_micro(g_factorFp);
        g_block->virtualElapsedQpc = (unsigned __int64)(LONGLONG)(g_virtualFp >> GS_FP_BITS);
    }
    gs_leave();
    return 1;
}

double __cdecl GameSpeed_Get(void)
{
    double f;
    gs_enter();
    gs_integrate(gs_now_real_qpc());
    f = gs_factor_from_fp(g_factorFp);
    gs_leave();
    return f;
}

void __cdecl GameSpeed_Reset(void)
{
    GameSpeed_Set(1.0);
}

unsigned int __cdecl GameSpeed_Version(void)
{
    return (1u << 16) | (unsigned int)GS_ARCH_BITS;
}
