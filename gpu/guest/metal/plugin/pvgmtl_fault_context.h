/* pvgmtl_fault_context.h - what was in flight when the device was lost, so a GPU fault carries names.

   A channel fault reports an address and a semaphore number and nothing else. This keeps three small rings, always, at
   the cost of a few stores per dispatch, submit and resource death: the last dispatches of each command buffer (kernel
   name and the storage views bound), the last submits (sequence number, so the fault's semaphore number maps to a
   submit), and the last image and heap-slot deaths (sequence number at death, so a kernel that was in flight while
   its image died is named). The report runs once, at device loss. Pure over its own state: no Vulkan calls, so
   tools/test_fault_context.c proves the matching on the host. The plugin's sequence number is its count of queue
   submits, which the NVK channel semaphore follows closely. */
#ifndef PVGMTL_FAULT_CONTEXT_H
#define PVGMTL_FAULT_CONTEXT_H
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <pthread.h>
#include <time.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>

enum { PVGMTL_FX_DISP = 8, PVGMTL_FX_STOR = 6, PVGMTL_FX_SUB = 48, PVGMTL_FX_GONE = 128, PVGMTL_FX_NAME = 40, PVGMTL_FX_ROOT = 12, PVGMTL_FX_BARS = 1024, PVGMTL_FX_COPIES = 4096 };
/* a buffer root of a dispatch: the GPU range bound at binding-table index idx (what the kernel may read or write) */
typedef struct { uint64_t lo, hi; uint32_t idx; } pvgmtl_fx_root;

/* one dispatch: its kernel and the storage views bound to it (explicit binds; a bindless read is not visible here) */
typedef struct { char name[PVGMTL_FX_NAME]; const void *pipe; uint32_t key; uint64_t air; uint32_t nstor; uint32_t stor_idx[PVGMTL_FX_STOR]; const void *stor_view[PVGMTL_FX_STOR];
                 const void *tab[2]; uint32_t dyn[2]; /* the binding-table buffer and offset in force per stage slot: the dispatch's root addresses */
                 uint32_t ordinal; uint32_t nroot; pvgmtl_fx_root root[PVGMTL_FX_ROOT]; const void *heap_set; /* the bindless heap set bound at encode */ } pvgmtl_fx_disp;
/* per command buffer: the last dispatches recorded into it, the storage binds and the binding tables in force */
typedef struct { pvgmtl_fx_disp d[PVGMTL_FX_DISP]; uint32_t n, total; uint32_t nstor; uint32_t stor_idx[PVGMTL_FX_STOR]; const void *stor_view[PVGMTL_FX_STOR];
                 const void *tab[2]; uint32_t dyn[2]; uint32_t nroot; pvgmtl_fx_root root[PVGMTL_FX_ROOT]; const void *heap_set; } pvgmtl_fx_cb;
/* one pipeline barrier: the command buffer it went into and the dispatch ordinal it followed (0 = before the first) */
typedef struct { const void *cb; uint32_t after; uint32_t ss, ds, sa, da, nm, nb, ni; } pvgmtl_fx_bar;
/* one buffer-to-buffer copy: where it wrote, where it read from (host view when the source is host-visible), when */
typedef struct { uint64_t dst_lo, dst_hi, src_lo, seq; const void *src_map; const void *cb; uint32_t ordinal; } pvgmtl_fx_copy;
/* one submit: the sequence number it got and the command buffer's dispatch ring as it was */
typedef struct { uint64_t seq; const void *cb; uint32_t ndisp; pvgmtl_fx_disp d[PVGMTL_FX_DISP]; uint32_t n; } pvgmtl_fx_sub;
/* one death: an image (view, size, format) or a heap slot, at a sequence number */
enum { PVGMTL_FX_PLACED = 1, PVGMTL_FX_POOLED = 2 };   /* where a dead image's memory lived */
typedef struct { uint64_t seq; const void *view; uint32_t slot, w, h, fmt, storage, where; char label[PVGMTL_FX_NAME]; } pvgmtl_fx_gone;
/* one buffer death: the GPU address range it held, at a sequence number. A page-table fault names an address and
   nothing else, so the ring of the last buffer deaths is what says whether that address was a buffer freed while a
   submit still read it. */
enum { PVGMTL_FX_BUFS = 4096, PVGMTL_FX_PARKED = 4 };   /* the game frees ~18 buffers per submit; 256 covered 14 submits */
typedef struct { uint64_t seq, lo, hi; uint32_t where; } pvgmtl_fx_buf;
typedef struct { pvgmtl_fx_sub sub[PVGMTL_FX_SUB]; uint32_t nsub; pvgmtl_fx_gone gone[PVGMTL_FX_GONE]; uint32_t ngone;
                 pvgmtl_fx_buf buf[PVGMTL_FX_BUFS]; uint32_t nbuf;
                 pvgmtl_fx_bar bar[PVGMTL_FX_BARS]; uint32_t nbar;
                 pvgmtl_fx_copy copy[PVGMTL_FX_COPIES]; uint32_t ncopy; } pvgmtl_fx_state;

static inline void pvgmtl_fx_copy_name(char *dst, const char *src) { snprintf(dst, PVGMTL_FX_NAME, "%s", src ? src : "-"); }

/* a storage view bound at index (NULL unbinds) */
static inline void pvgmtl_fx_bind_storage(pvgmtl_fx_cb *c, uint32_t index, const void *view)
{
    for (uint32_t k = 0; k < c->nstor; k++) if (c->stor_idx[k] == index) {
        if (view) { c->stor_view[k] = view; return; }
        c->stor_idx[k] = c->stor_idx[c->nstor - 1]; c->stor_view[k] = c->stor_view[c->nstor - 1]; c->nstor--; return;
    }
    if (!view) return;
    if (c->nstor < PVGMTL_FX_STOR) { c->stor_idx[c->nstor] = index; c->stor_view[c->nstor] = view; c->nstor++; }
    else { c->stor_idx[PVGMTL_FX_STOR - 1] = index; c->stor_view[PVGMTL_FX_STOR - 1] = view; }   /* the newest keeps the last place */
}
/* a dispatch: the kernel name and the storage binds in force go into the command buffer's ring */
static inline void pvgmtl_fx_dispatch(pvgmtl_fx_cb *c, const char *name, const void *pipe, uint32_t key, uint64_t air)
{
    pvgmtl_fx_disp *d = &c->d[c->n % PVGMTL_FX_DISP];
    pvgmtl_fx_copy_name(d->name, name); d->pipe = pipe; d->key = key; d->air = air;
    d->nstor = c->nstor; memcpy(d->stor_idx, c->stor_idx, sizeof d->stor_idx); memcpy(d->stor_view, c->stor_view, sizeof d->stor_view);
    d->tab[0] = c->tab[0]; d->tab[1] = c->tab[1]; d->dyn[0] = c->dyn[0]; d->dyn[1] = c->dyn[1];
    d->ordinal = c->total + 1; d->nroot = c->nroot < PVGMTL_FX_ROOT ? c->nroot : PVGMTL_FX_ROOT; memcpy(d->root, c->root, sizeof d->root); d->heap_set = c->heap_set;
    c->n++; c->total++;
}
/* a submit of command buffer cb at sequence number seq */
static inline void pvgmtl_fx_submit(pvgmtl_fx_state *s, uint64_t seq, const void *cb, const pvgmtl_fx_cb *c)
{
    pvgmtl_fx_sub *u = &s->sub[s->nsub % PVGMTL_FX_SUB];
    u->seq = seq; u->cb = cb; u->ndisp = c ? c->total : 0; u->n = c ? c->n : 0;
    if (c) memcpy(u->d, c->d, sizeof u->d); else memset(u->d, 0, sizeof u->d);
    s->nsub++;
}
static inline void pvgmtl_fx_image_gone(pvgmtl_fx_state *s, uint64_t seq, const void *view, uint32_t w, uint32_t h, uint32_t fmt, uint32_t storage, uint32_t where, const char *label)
{
    pvgmtl_fx_gone *g = &s->gone[s->ngone % PVGMTL_FX_GONE];
    g->seq = seq; g->view = view; g->slot = 0; g->w = w; g->h = h; g->fmt = fmt; g->storage = storage; g->where = where; pvgmtl_fx_copy_name(g->label, label);
    s->ngone++;
}
static inline void pvgmtl_fx_slot_gone(pvgmtl_fx_state *s, uint64_t seq, uint32_t slot)
{
    pvgmtl_fx_gone *g = &s->gone[s->ngone % PVGMTL_FX_GONE];
    memset(g, 0, sizeof *g); g->seq = seq; g->slot = slot; pvgmtl_fx_copy_name(g->label, "heap slot");
    s->ngone++;
}
/* a buffer whose GPU address range [lo, lo + size) was released at sequence number seq */
static inline void pvgmtl_fx_buffer_gone(pvgmtl_fx_state *s, uint64_t seq, uint64_t lo, uint64_t size, uint32_t where)
{
    if (!lo) return;   /* a buffer that never had a device address cannot be the one a fault names */
    pvgmtl_fx_buf *b = &s->buf[s->nbuf % PVGMTL_FX_BUFS];
    b->seq = seq; b->lo = lo; b->hi = lo + size; b->where = where;
    s->nbuf++;
}
/* the newest recorded buffer death whose range held addr, or NULL: the question a FAULT_PTE address asks */
static inline const pvgmtl_fx_buf *pvgmtl_fx_buffer_at(const pvgmtl_fx_state *s, uint64_t addr)
{
    const uint32_t nb = s->nbuf < PVGMTL_FX_BUFS ? s->nbuf : PVGMTL_FX_BUFS;
    for (uint32_t k = 0; k < nb; k++) { const pvgmtl_fx_buf *b = &s->buf[(s->nbuf - 1 - k) % PVGMTL_FX_BUFS]; if (addr >= b->lo && addr < b->hi) return b; }
    return NULL;
}

/* a pipeline barrier recorded into cb after dispatch `after` of that command buffer */
static inline void pvgmtl_fx_barrier(pvgmtl_fx_state *s, const void *cb, uint32_t after, uint32_t ss, uint32_t ds, uint32_t sa, uint32_t da, uint32_t nm, uint32_t nb, uint32_t ni)
{
    pvgmtl_fx_bar *b = &s->bar[s->nbar % PVGMTL_FX_BARS];
    b->cb = cb; b->after = after; b->ss = ss; b->ds = ds; b->sa = sa; b->da = da; b->nm = nm; b->nb = nb; b->ni = ni;
    s->nbar++;
}
/* a copy of size bytes into dst_lo from src_lo (host view src_map when the source is host-visible), recorded into cb after dispatch ordinal */
static inline void pvgmtl_fx_copy_note(pvgmtl_fx_state *s, uint64_t dst_lo, uint64_t size, uint64_t src_lo, const void *src_map, uint64_t seq, const void *cb, uint32_t ordinal)
{
    if (!dst_lo) return;
    pvgmtl_fx_copy *c = &s->copy[s->ncopy % PVGMTL_FX_COPIES];
    c->dst_lo = dst_lo; c->dst_hi = dst_lo + size; c->src_lo = src_lo; c->src_map = src_map; c->seq = seq; c->cb = cb; c->ordinal = ordinal;
    s->ncopy++;
}
/* the newest copy whose destination covers [lo, hi) whole, or NULL */
static inline const pvgmtl_fx_copy *pvgmtl_fx_copy_covering(const pvgmtl_fx_state *s, uint64_t lo, uint64_t hi)
{
    const uint32_t n = s->ncopy < PVGMTL_FX_COPIES ? s->ncopy : PVGMTL_FX_COPIES;
    for (uint32_t k = 0; k < n; k++) { const pvgmtl_fx_copy *c = &s->copy[(s->ncopy - 1 - k) % PVGMTL_FX_COPIES]; if (c->dst_lo <= lo && hi <= c->dst_hi) return c; }
    return NULL;
}
static inline const char *pvgmtl_fx_stage_name(uint32_t f, char *buf, size_t cap)
{
    switch (f) {
    case 0x1: return "TOP"; case 0x8: return "VERTEX"; case 0x80: return "FRAGMENT"; case 0x400: return "COLOR_OUT"; case 0x800: return "COMPUTE";
    case 0x1000: return "TRANSFER"; case 0x2000: return "BOTTOM"; case 0x8000: return "ALL_GRAPHICS"; case 0x10000: return "ALL_COMMANDS";
    default: snprintf(buf, cap, "0x%x", f); return buf; }
}
static inline int pvgmtl_fx_access_name(uint32_t f, char *buf, size_t cap)
{
    static const struct { uint32_t bit; const char *n; } names[] = { {0x8,"UNIFORM_READ"}, {0x20,"SHADER_READ"}, {0x40,"SHADER_WRITE"}, {0x80,"COLOR_READ"}, {0x100,"COLOR_WRITE"},
        {0x200,"DS_READ"}, {0x400,"DS_WRITE"}, {0x800,"TRANSFER_READ"}, {0x1000,"TRANSFER_WRITE"}, {0x8000,"MEMORY_READ"}, {0x10000,"MEMORY_WRITE"} };
    int o = 0; uint32_t rest = f;
    for (unsigned k = 0; k < sizeof names / sizeof names[0]; k++) if (f & names[k].bit) { o += snprintf(buf + o, cap - (size_t)o, "%s%s", o ? "|" : "", names[k].n); rest &= ~names[k].bit; }
    if (rest || !o) o += snprintf(buf + o, cap - (size_t)o, "%s0x%x", o ? "|" : "", rest);
    return o;
}
/* The barriers recorded into cb after dispatch `after` (before `after` + 1): one line each, or one "NONE" line. Returns how many. */
static inline unsigned pvgmtl_fx_barriers_after(const pvgmtl_fx_state *s, const void *cb, uint32_t after, void (*emit)(void *, const char *), void *ctx)
{
    char line[400], sb[32], db[32], sa[160], da[160]; unsigned n = 0;
    const uint32_t nb = s->nbar < PVGMTL_FX_BARS ? s->nbar : PVGMTL_FX_BARS;
    for (uint32_t k = 0; k < nb; k++) {
        const pvgmtl_fx_bar *b = &s->bar[(s->nbar - nb + k) % PVGMTL_FX_BARS];   /* oldest first */
        if (b->cb != cb || b->after != after) continue;
        pvgmtl_fx_access_name(b->sa, sa, sizeof sa); pvgmtl_fx_access_name(b->da, da, sizeof da);
        snprintf(line, sizeof line, "fault context: barrier after dispatch %u: %s -> %s, %u memory [%s -> %s], %u buffer, %u image", after,
                 pvgmtl_fx_stage_name(b->ss, sb, sizeof sb), pvgmtl_fx_stage_name(b->ds, db, sizeof db), b->nm, b->nm ? sa : "-", b->nm ? da : "-", b->nb, b->ni);
        emit(ctx, line); n++;
    }
    if (!n) { snprintf(line, sizeof line, "fault context: barrier after dispatch %u: NONE", after); emit(ctx, line); }
    return n;
}
/* Who wrote (bound) the range [lo, hi) before dispatch `ordinal` of the command buffer whose ring is `me` (submit sequence `seq`):
   earlier dispatches of the same command buffer, every dispatch of the two submits before it, and every copy into the range.
   One line; returns how many writers it named. */
static inline unsigned pvgmtl_fx_writers(const pvgmtl_fx_state *s, const pvgmtl_fx_cb *me, const void *cb, uint64_t seq, uint32_t ordinal, uint32_t idx, uint64_t lo, uint64_t hi,
                                        void (*emit)(void *, const char *), void *ctx)
{
    char line[600]; int o = snprintf(line, sizeof line, "fault context: root b%u 0x%llx..0x%llx of dispatch %u bound earlier by:", idx, (unsigned long long)lo, (unsigned long long)hi, ordinal);
    unsigned n = 0;
    const uint32_t nd = me->n < PVGMTL_FX_DISP ? me->n : PVGMTL_FX_DISP;
    for (uint32_t j = 0; j < nd && o < (int)sizeof line - 80; j++) {
        const pvgmtl_fx_disp *d = &me->d[(me->n - nd + j) % PVGMTL_FX_DISP];
        if (d->ordinal >= ordinal) continue;
        for (uint32_t r = 0; r < d->nroot; r++) if (d->root[r].lo < hi && lo < d->root[r].hi) {
            o += snprintf(line + o, sizeof line - (size_t)o, " %s %08x (dispatch %u, this cb, b%u)", d->name, d->key, d->ordinal, d->root[r].idx); n++; break; }
    }
    const uint32_t ns = s->nsub < PVGMTL_FX_SUB ? s->nsub : PVGMTL_FX_SUB;
    for (uint32_t k = 0; k < ns && o < (int)sizeof line - 80; k++) {
        const pvgmtl_fx_sub *u = &s->sub[(s->nsub - 1 - k) % PVGMTL_FX_SUB];
        if (u->seq + 2 < seq || u->seq >= seq) continue;
        const uint32_t un = u->n < PVGMTL_FX_DISP ? u->n : PVGMTL_FX_DISP;
        for (uint32_t j = 0; j < un && o < (int)sizeof line - 80; j++) {
            const pvgmtl_fx_disp *d = &u->d[(u->n - un + j) % PVGMTL_FX_DISP];
            for (uint32_t r = 0; r < d->nroot; r++) if (d->root[r].lo < hi && lo < d->root[r].hi) {
                o += snprintf(line + o, sizeof line - (size_t)o, " %s %08x (dispatch %u, seq %llu, b%u)", d->name, d->key, d->ordinal, (unsigned long long)u->seq, d->root[r].idx); n++; break; }
        }
    }
    unsigned copies = 0; const pvgmtl_fx_copy *last = NULL;
    const uint32_t nc = s->ncopy < PVGMTL_FX_COPIES ? s->ncopy : PVGMTL_FX_COPIES;
    for (uint32_t k = 0; k < nc; k++) {
        const pvgmtl_fx_copy *c = &s->copy[(s->ncopy - 1 - k) % PVGMTL_FX_COPIES];
        if (!(c->dst_lo < hi && lo < c->dst_hi)) continue;
        if (c->cb == cb ? c->ordinal >= ordinal : (c->seq + 2 < seq || c->seq >= seq)) continue;
        if (!last) last = c; copies++;
    }
    if (last) o += snprintf(line + o, sizeof line - (size_t)o, " | %u copies into it, newest 0x%llx..0x%llx at seq %llu after dispatch %u%s", copies,
                            (unsigned long long)last->dst_lo, (unsigned long long)last->dst_hi, (unsigned long long)last->seq, last->ordinal, last->cb == cb ? " (this cb)" : "");
    else o += snprintf(line + o, sizeof line - (size_t)o, "%s | 0 copies", n ? "" : " nothing recorded");
    emit(ctx, line);
    return n + copies;
}

/* A lock the device-loss report takes: at most `ms` milliseconds, then 0 - the report proceeds without it (read-only,
   a torn read accepted) rather than hang the process it is reporting on. A mutex this thread already holds also returns 0. */
static inline int pvgmtl_fx_lock_bounded(pthread_mutex_t *m, unsigned ms)
{
    for (unsigned t = 0; t < ms * 10u; t++) { if (pthread_mutex_trylock(m) == 0) return 1; struct timespec ts = { 0, 100000 }; nanosleep(&ts, NULL); }
    return 0;
}
/* A host read through the kernel: an unmapped or bus-faulting mapping is an error (0), never a signal. */
static inline int pvgmtl_fx_read_safe(const void *src, void *dst, size_t n)
{
    mach_vm_size_t got = 0;
    return mach_vm_read_overwrite(mach_task_self(), (mach_vm_address_t)(uintptr_t)src, n, (mach_vm_address_t)(uintptr_t)dst, &got) == KERN_SUCCESS && got == n;
}

/* The report: calls emit(line) once per line. The last `subs` submits, newest first, with their dispatches; every
   dispatch whose storage view belongs to an image that died at or before that submit is marked DEAD; then the deaths
   of the last `window` sequence numbers. Returns how many DEAD marks it made. */
typedef void (*pvgmtl_fx_emit)(void *ctx, const char *line);
static inline unsigned pvgmtl_fx_report(const pvgmtl_fx_state *s, uint64_t now_seq, uint32_t subs, uint64_t window, pvgmtl_fx_emit emit, void *ctx)
{
    char line[512]; unsigned dead = 0;
    const uint32_t ns = s->nsub < PVGMTL_FX_SUB ? s->nsub : PVGMTL_FX_SUB;
    const uint32_t ng = s->ngone < PVGMTL_FX_GONE ? s->ngone : PVGMTL_FX_GONE;
    snprintf(line, sizeof line, "fault context: %u submits recorded, last sequence %llu; %u deaths recorded (sequence = this process's queue submits; the channel semaphore follows it)",
             s->nsub, (unsigned long long)now_seq, s->ngone);
    emit(ctx, line);
    for (uint32_t k = 0; k < ns && k < subs; k++) {
        const pvgmtl_fx_sub *u = &s->sub[(s->nsub - 1 - k) % PVGMTL_FX_SUB];
        snprintf(line, sizeof line, "fault context: submit seq %llu cb %p: %u dispatches in all", (unsigned long long)u->seq, u->cb, u->ndisp);
        emit(ctx, line);
        const uint32_t nd = u->n < PVGMTL_FX_DISP ? u->n : PVGMTL_FX_DISP;
        for (uint32_t j = 0; j < nd; j++) {
            const pvgmtl_fx_disp *d = &u->d[(u->n - nd + j) % PVGMTL_FX_DISP];
            int o = snprintf(line, sizeof line, "fault context:   dispatch %u %s spv-fnv %08x air %016llx pipe %p", u->ndisp - nd + j + 1, d->name, d->key, (unsigned long long)d->air, d->pipe);
            for (uint32_t m = 0; m < d->nstor && m < PVGMTL_FX_STOR; m++) {
                const pvgmtl_fx_gone *g = NULL;
                for (uint32_t q = 0; q < ng; q++) { const pvgmtl_fx_gone *c = &s->gone[q]; if (c->view && c->view == d->stor_view[m] && c->seq <= u->seq) { g = c; break; } }
                if (g) { dead++; o += snprintf(line + o, sizeof line - (size_t)o, " | storage %u view %p DEAD: image %ux%u fmt %u%s \"%s\" died at seq %llu",
                                               d->stor_idx[m], d->stor_view[m], g->w, g->h, g->fmt, g->storage ? " storage" : "", g->label, (unsigned long long)g->seq); }
                else o += snprintf(line + o, sizeof line - (size_t)o, " | storage %u view %p", d->stor_idx[m], d->stor_view[m]);
                if (o >= (int)sizeof line) break;
            }
            emit(ctx, line);
        }
    }
    unsigned shown = 0;
    for (uint32_t k = 0; k < ng; k++) {
        const pvgmtl_fx_gone *g = &s->gone[(s->ngone - 1 - k) % PVGMTL_FX_GONE];
        if (now_seq > window && g->seq + window < now_seq) continue;
        if (g->slot) snprintf(line, sizeof line, "fault context: died at seq %llu: heap slot %u", (unsigned long long)g->seq, g->slot);
        else snprintf(line, sizeof line, "fault context: died at seq %llu: image view %p %ux%u fmt %u%s%s \"%s\"", (unsigned long long)g->seq, g->view, g->w, g->h, g->fmt, g->storage ? " storage" : "",
                      g->where & PVGMTL_FX_POOLED ? " pooled" : g->where & PVGMTL_FX_PLACED ? " placed" : " own memory", g->label);
        emit(ctx, line); shown++;
    }
    snprintf(line, sizeof line, "fault context: %u deaths within the last %llu submits, %u dispatches bound a dead image", shown, (unsigned long long)window, dead);
    emit(ctx, line);
    /* the buffer deaths of the window, newest first, four to a line: match the fault's address against these ranges */
    {   const uint32_t nb = s->nbuf < PVGMTL_FX_BUFS ? s->nbuf : PVGMTL_FX_BUFS;
        unsigned nshown = 0, per = 0; int o = 0;
        for (uint32_t k = 0; k < nb; k++) {
            const pvgmtl_fx_buf *b = &s->buf[(s->nbuf - 1 - k) % PVGMTL_FX_BUFS];
            if (now_seq > window && b->seq + window < now_seq) continue;
            if (!per) o = snprintf(line, sizeof line, "fault context: dead buffers (seq GPU lo..hi KB):");
            o += snprintf(line + o, sizeof line - (size_t)o, " %llu 0x%llx..0x%llx %llu%s%s%s |", (unsigned long long)b->seq, (unsigned long long)b->lo,
                          (unsigned long long)b->hi, (unsigned long long)((b->hi - b->lo) >> 10),
                          b->where & PVGMTL_FX_POOLED ? " pooled" : "", b->where & PVGMTL_FX_PLACED ? " placed" : "", b->where & PVGMTL_FX_PARKED ? " parked" : "");
            nshown++;
            if (++per == 4 || o >= (int)sizeof line - 80) { emit(ctx, line); per = 0; }
            if (nshown == 64) break;   /* the log shows the newest 64; the fault file at device loss holds the whole ring */
        }
        if (per) emit(ctx, line);
        unsigned inwin = 0;
        for (uint32_t k = 0; k < nb; k++) { const pvgmtl_fx_buf *b = &s->buf[(s->nbuf - 1 - k) % PVGMTL_FX_BUFS]; if (!(now_seq > window && b->seq + window < now_seq)) inwin++; }
        snprintf(line, sizeof line, "fault context: %u of %u buffer deaths within the last %llu submits shown (%u recorded in all)", nshown, inwin, (unsigned long long)window, s->nbuf);
        emit(ctx, line);
    }
    return dead;
}
#endif
