/*
 * NullMoth NVIDIA driver for macOS
 * Copyright (c) 2026 NullMoth Systems.
 * SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
 */

#import <objc/runtime.h>

#import <os/lock.h>

#define PVGMTL_RESID_SAMPLER (1ull << 63)
#define PVGMTL_RESID_HEAP    (1ull << 62)

_Static_assert(sizeof(uintptr_t) == sizeof(uint64_t), "resource ID keys require 64-bit pointers");
// An argument buffer owned by a Managed buffer: its texture and sampler handles are read from the CPU copy and the
// embedded slots bound (default), or the slot is refused and cleared to the dummy as it was before the ray-tracing
// batch (PVGMTL_EMBEDDED_MANAGED=0). One switch so a run can split the two.
static int pvgmtl_embedded_managed_on(void)
{
    static int on = -1;
    if (on < 0) { const char *e = getenv("PVGMTL_EMBEDDED_MANAGED"); on = !(e && e[0] == '0');
        if (!on) nvlog("embedded: argument buffers owned by Managed buffers are REFUSED as before (PVGMTL_EMBEDDED_MANAGED=0)"); }
    return on;
}
static NSMapTable *pvgmtl_resid_map;
static os_unfair_lock pvgmtl_resid_lock = OS_UNFAIR_LOCK_INIT;
static uint64_t pvgmtl_resid_seq;

static uint64_t pvgmtl_resid_assign(id resource, uint64_t tag, uint32_t heapSlot)
{
    if (!resource) return 0;
    os_unfair_lock_lock(&pvgmtl_resid_lock);
    if (!pvgmtl_resid_map) pvgmtl_resid_map = [[NSMapTable alloc]
        initWithKeyOptions:NSPointerFunctionsOpaqueMemory | NSPointerFunctionsIntegerPersonality
        valueOptions:NSPointerFunctionsWeakMemory capacity:0];
    // A token without a slot keeps its low 17 bits zero: the translated shader masks a handle's low word to a heap slot
    // (BINDLESS_HEAP_SLOTS = 131072) with no way to tell the two apart, so a bare sequence number there made a kernel read
    // or write through another texture's slot, or a slot whose storage descriptor nobody had written. Slot 0 is the dummy.
    uint64_t token = heapSlot ? (PVGMTL_RESID_HEAP | heapSlot) : (((++pvgmtl_resid_seq) << 17) | tag);
    [pvgmtl_resid_map setObject:resource forKey:(__bridge id)(void *)(uintptr_t)token];
    uint64_t censusIssued = 0; NSUInteger censusLive = 0; BOOL doCensus = ((pvgmtl_resid_seq & 0xFFF) == 0);
    if (doCensus) { censusIssued = pvgmtl_resid_seq & ~PVGMTL_RESID_SAMPLER; censusLive = pvgmtl_resid_map.count; }
    os_unfair_lock_unlock(&pvgmtl_resid_lock);
    if (doCensus)
        nvlog("resid census: %llu handles issued, %lu still live in the table",
              (unsigned long long)censusIssued, (unsigned long)censusLive);
    return token;
}

static id pvgmtl_resid_lookup(uint64_t token)
{
    if (!token) return nil;
    os_unfair_lock_lock(&pvgmtl_resid_lock);
    id resource = [pvgmtl_resid_map objectForKey:(__bridge id)(void *)(uintptr_t)token];
    os_unfair_lock_unlock(&pvgmtl_resid_lock);
    return resource;
}

// The same slot carries the storage descriptor (heap binding 2) for a texture a kernel may write - a shader reaches a
// written texture only through this handle in memory.
// A texture that gets no slot is read by a shader as slot 0, the dummy (pvgmtl_resid_assign): its reads come back black
// and its writes are lost. The first 8 are logged verbatim with the reason, then a census every 256, so a run shows
// the population instead of guessing it.
static void pvgmtl_resid_no_slot(PVGMTLTexture *x, const char *why)
{
    static _Atomic unsigned long long n; const unsigned long long k = atomic_fetch_add(&n, 1) + 1;
    if (k <= 8) nvlog("resid: NO SLOT #%llu for texture %p %ux%u type %lu pixelFormat %lu usage 0x%lx storage %lu mips %u stex %d - %s",
                     k, (__bridge void *)x, x->_i.w, x->_i.h, (unsigned long)x.textureType, (unsigned long)x->_fmt, (unsigned long)x->_usage,
                     (unsigned long)x.storageMode, x->_i.mips, x->_stex ? 1 : 0, why);
    else if ((k & 255) == 0) nvlog("resid: NO SLOT census - %llu textures so far read as the dummy (last: %s)", k, why);
}
uint64_t pvgmtl_resid_for_texture(id t)
{
    PVGMTLTexture *x = (PVGMTLTexture *)t;
    // a texture buffer has no image view: its slot holds the buffer view in the texel bindings instead
    if (x->_tbView) {
        const uint32_t ts = pvgmtl_vk_bindless_put_texel(x->_tbView, x->_tbStorage);
        if (!ts) pvgmtl_resid_no_slot(x, "texture buffer: the heap has no texel bindings (PVGMTL_NO_HEAP_STORAGE) or no buffer view");
        return pvgmtl_resid_assign(t, 0, ts);
    }
    void *sample = [x nvsampleview];
    const uint32_t slot = pvgmtl_vk_bindless_put(sample);
    if (slot) pvgmtl_vk_bindless_put_storage(slot, x->_stex ? [x nvview] : NULL);
    else pvgmtl_resid_no_slot(x, sample ? "heap full, or no heap in this process" : "the image has no sample view");
    return pvgmtl_resid_assign(t, 0, slot);
}
// A sampler's gpuResourceID carries its bindless heap slot in the low 32 bits (what a shader reading an argument buffer
// indexes the sampler heap with) and a sequence number above them, so every sampler object keeps a unique token.
uint64_t pvgmtl_resid_for_sampler(id s)
{
    const uint32_t slot = pvgmtl_vk_bindless_sampler_slot(((PVGMTLSamplerState *)s)->_s.s);
    if (!slot) return pvgmtl_resid_assign(s, PVGMTL_RESID_SAMPLER, 0);
    os_unfair_lock_lock(&pvgmtl_resid_lock);
    if (!pvgmtl_resid_map) pvgmtl_resid_map = [[NSMapTable alloc]
        initWithKeyOptions:NSPointerFunctionsOpaqueMemory | NSPointerFunctionsIntegerPersonality
        valueOptions:NSPointerFunctionsWeakMemory capacity:0];
    const uint64_t token = PVGMTL_RESID_SAMPLER | ((++pvgmtl_resid_seq & 0x3fffffffull) << 32) | slot;
    [pvgmtl_resid_map setObject:s forKey:(__bridge id)(void *)(uintptr_t)token];
    os_unfair_lock_unlock(&pvgmtl_resid_lock);
    return token;
}
void pvgmtl_resid_texture_gone(uint64_t token) { if (token & PVGMTL_RESID_HEAP) pvgmtl_vk_bindless_drop((uint32_t)(token & 0xffffffffu)); }
// For the fault report: the live texture object whose gpuResourceID carries this heap slot, or that none does.
int pvgmtl_resid_describe_slot(uint32_t slot, char *out, size_t cap)
{
    id o = pvgmtl_resid_lookup(PVGMTL_RESID_HEAP | slot);
    if (!o) { snprintf(out, cap, "no live texture object holds slot %u", slot); return 0; }
    if ([o isKindOfClass:[PVGMTLTexture class]]) {
        PVGMTLTexture *t = o;
        if (t->_parent || t->_viewParent) {
            PVGMTLTexture *p = t->_parent ? t->_parent : t->_viewParent; nvk_image *pi = [p nvi];
            snprintf(out, cap, "VIEW PVGMTLTexture %p (mtl fmt %lu type %lu usage 0x%lx storage view %s own view %p swizzle view %p) of parent PVGMTLTexture %p (%ux%u vkfmt %u image %p view %p \"%s\")",
                     (__bridge void *)t, (unsigned long)t->_fmt, (unsigned long)t.textureType, (unsigned long)t.usage, t->_stex ? "yes" : "no", t->_ownView, t->_swzView,
                     (__bridge void *)p, pi->w, pi->h, pi->fmt, pi->img, pi->view, p.label.UTF8String ?: "");
            return 1;
        }
        snprintf(out, cap, "PVGMTLTexture %p %ux%u vkfmt %u mtl fmt %lu type %lu usage 0x%lx storage view %s image %p view %p \"%s\"", (__bridge void *)t, t->_i.w, t->_i.h, t->_i.fmt,
                 (unsigned long)t->_fmt, (unsigned long)t.textureType, (unsigned long)t.usage, t->_stex ? "yes" : "no", t->_i.img, t->_i.view, t.label.UTF8String ?: "");
        return 1;
    }
    snprintf(out, cap, "%s %p holds slot %u", object_getClassName(o), (__bridge void *)o, slot);
    return 1;
}

static NSUInteger pvgmtl_ss_enum(NSDictionary *st, NSString *key, NSDictionary<NSString *, NSNumber *> *map, NSUInteger fallback, BOOL *bad)
{
    id v = st[key];
    if (![v isKindOfClass:[NSString class]]) return fallback;
    NSNumber *n = map[v];
    if (!n) { *bad = YES; nvlog("static sampler: %s = %s has no Metal equivalent here", key.UTF8String, [v UTF8String]); return fallback; }
    return n.unsignedIntegerValue;
}
static id<MTLSamplerState> pvgmtl_static_sampler_from(NSDictionary *st)
{
    NSDictionary *filt = @{ @"Nearest": @(MTLSamplerMinMagFilterNearest), @"Linear": @(MTLSamplerMinMagFilterLinear) };
    NSDictionary *mip = @{ @"None": @(MTLSamplerMipFilterNotMipmapped), @"Nearest": @(MTLSamplerMipFilterNearest),
                           @"Linear": @(MTLSamplerMipFilterLinear) };
    NSDictionary *addr = @{ @"ClampToEdge": @(MTLSamplerAddressModeClampToEdge), @"Repeat": @(MTLSamplerAddressModeRepeat),
                            @"MirroredRepeat": @(MTLSamplerAddressModeMirrorRepeat), @"MirrorRepeat": @(MTLSamplerAddressModeMirrorRepeat), @"ClampToZero": @(MTLSamplerAddressModeClampToZero),
                            @"ClampToBorder": @(MTLSamplerAddressModeClampToBorderColor),
                            @"MirrorClampToEdge": @(MTLSamplerAddressModeMirrorClampToEdge) };
    NSDictionary *cmp = @{ @"Never": @(MTLCompareFunctionNever), @"Less": @(MTLCompareFunctionLess), @"Equal": @(MTLCompareFunctionEqual),
                           @"LessEqual": @(MTLCompareFunctionLessEqual), @"Greater": @(MTLCompareFunctionGreater),
                           @"NotEqual": @(MTLCompareFunctionNotEqual), @"GreaterEqual": @(MTLCompareFunctionGreaterEqual),
                           @"Always": @(MTLCompareFunctionAlways), @"None": @(MTLCompareFunctionNever) };
    NSDictionary *border = @{ @"TransparentBlack": @(MTLSamplerBorderColorTransparentBlack), @"OpaqueBlack": @(MTLSamplerBorderColorOpaqueBlack),
                              @"OpaqueWhite": @(MTLSamplerBorderColorOpaqueWhite) };
    BOOL bad = NO;
    MTLSamplerDescriptor *d = [MTLSamplerDescriptor new];
    d.minFilter = (MTLSamplerMinMagFilter)pvgmtl_ss_enum(st, @"min_filter", filt, MTLSamplerMinMagFilterNearest, &bad);
    d.magFilter = (MTLSamplerMinMagFilter)pvgmtl_ss_enum(st, @"mag_filter", filt, MTLSamplerMinMagFilterNearest, &bad);
    d.mipFilter = (MTLSamplerMipFilter)pvgmtl_ss_enum(st, @"mip_filter", mip, MTLSamplerMipFilterNotMipmapped, &bad);
    d.sAddressMode = (MTLSamplerAddressMode)pvgmtl_ss_enum(st, @"address_mode_s", addr, MTLSamplerAddressModeClampToEdge, &bad);
    d.tAddressMode = (MTLSamplerAddressMode)pvgmtl_ss_enum(st, @"address_mode_t", addr, MTLSamplerAddressModeClampToEdge, &bad);
    d.rAddressMode = (MTLSamplerAddressMode)pvgmtl_ss_enum(st, @"address_mode_r", addr, MTLSamplerAddressModeClampToEdge, &bad);
    d.compareFunction = (MTLCompareFunction)pvgmtl_ss_enum(st, @"compare_function", cmp, MTLCompareFunctionNever, &bad);
    d.borderColor = (MTLSamplerBorderColor)pvgmtl_ss_enum(st, @"border_color", border, MTLSamplerBorderColorTransparentBlack, &bad);
    d.normalizedCoordinates = ![st[@"coordinates"] isEqual:@"Pixel"];
    NSNumber *an = st[@"max_anisotropy"], *lo = st[@"lod_min_clamp"], *hi = st[@"lod_max_clamp"];
    if ([an isKindOfClass:[NSNumber class]]) d.maxAnisotropy = MAX(1u, an.unsignedIntegerValue);
    if ([lo isKindOfClass:[NSNumber class]]) d.lodMinClamp = lo.floatValue;
    if ([hi isKindOfClass:[NSNumber class]]) d.lodMaxClamp = hi.floatValue;
    if (bad) return nil;
    return [MTLCreateSystemDefaultDevice() newSamplerStateWithDescriptor:d];
}
static const void *PVGMTL_SSAMP_KEY = &PVGMTL_SSAMP_KEY;
static NSArray<NSArray *> *pvgmtl_static_samplers(PVGMTLFunction *fn)
{
    NSArray *cached = objc_getAssociatedObject(fn, PVGMTL_SSAMP_KEY);
    if (cached) return cached;
    NSMutableArray *out = [NSMutableArray new];
    @synchronized (fn) {
        cached = objc_getAssociatedObject(fn, PVGMTL_SSAMP_KEY);
        if (cached) return cached;
        char e[512] = { 0 };
        NSData *d = (fn->_air && fn->_stage.length && pvgmtl_xlate_ready() && xlate_refl)
                  ? pvgmtl_reflect_air_cached(fn, fn->_air, fn->_stage, e, sizeof e) : nil;
        NSDictionary *refl = d ? [NSJSONSerialization JSONObjectWithData:d options:0 error:NULL] : nil;
        NSArray *all = [refl isKindOfClass:[NSDictionary class]] ? refl[@"bindings"] : nil;
        for (NSDictionary *b in [all isKindOfClass:[NSArray class]] ? all : @[]) {
            if (![b isKindOfClass:[NSDictionary class]] || ![b[@"kind"] isEqual:@"StaticSampler"]) continue;
            NSNumber *slot = b[@"metal_index"]; NSDictionary *st = b[@"static_sampler"];
            if (![slot isKindOfClass:[NSNumber class]] || ![st isKindOfClass:[NSDictionary class]]) continue;
            id<MTLSamplerState> s = pvgmtl_static_sampler_from(st);
            if (s) [out addObject:@[slot, s]];
            else nvlog("static sampler: %s slot %@ NOT created - it keeps the dummy sampler", fn->_fname.UTF8String, slot);
        }
        if (out.count) nvlog("static sampler: %s binds %lu constexpr sampler(s) with their own state", fn->_fname.UTF8String, (unsigned long)out.count);
        objc_setAssociatedObject(fn, PVGMTL_SSAMP_KEY, out, OBJC_ASSOCIATION_RETAIN);
    }
    return out;
}
static int pvgmtl_static_samplers_on(void)
{
    static int on = -1;
    if (on < 0) { on = !getenv("PVGMTL_NO_STATIC_SAMPLERS");
        nvlog("static sampler: constexpr samplers %s (PVGMTL_NO_STATIC_SAMPLERS=%s)", on ? "bind their own state" : "keep the dummy sampler (OLD)",
              getenv("PVGMTL_NO_STATIC_SAMPLERS") ?: "unset"); }
    return on;
}

@interface PVGMTLEmbeddedPlan : NSObject {
@public
    BOOL wantSampler, storage;
    uint32_t slot;
    NSUInteger ownerIndex, fieldOffset;
    uint32_t dset, dbinding;
    NSString *bindingText;
}
@end
@implementation PVGMTLEmbeddedPlan
@end
static char pvgmtl_embedded_plan_key;
static BOOL pvgmtl_spirv_declared(NSData *spv, uint64_t decl[2][8])
{
    memset(decl, 0, 2 * 8 * sizeof(uint64_t));
    const uint32_t *w = (const uint32_t *)spv.bytes; size_t n = spv.length / 4;
    if (!w || n < 5 || w[0] != 0x07230203u) return NO;
    uint32_t bound = w[3]; if (!bound || bound > (1u << 22)) return NO;
    int32_t *set = (int32_t *)malloc(bound * sizeof *set), *bind = (int32_t *)malloc(bound * sizeof *bind);
    if (!set || !bind) { free(set); free(bind); return NO; }
    for (uint32_t k = 0; k < bound; k++) { set[k] = -1; bind[k] = -1; }
    for (size_t i = 5; i < n; ) {
        uint32_t wc = w[i] >> 16, op = w[i] & 0xffffu;
        if (!wc || i + wc > n) { free(set); free(bind); return NO; }
        if (op == 71 && wc >= 4 && w[i + 1] < bound) {
            if (w[i + 2] == 34) set[w[i + 1]] = (int32_t)w[i + 3]; else if (w[i + 2] == 33) bind[w[i + 1]] = (int32_t)w[i + 3];
        }
        i += wc;
    }
    for (uint32_t k = 0; k < bound; k++)
        if ((set[k] == 0 || set[k] == 1) && bind[k] >= 0 && bind[k] < 512) decl[set[k]][bind[k] >> 6] |= 1ull << (bind[k] & 63);
    free(set); free(bind); return YES;
}
static NSArray<PVGMTLEmbeddedPlan *> *pvgmtl_embedded_plan(PVGMTLFunction *fn) {
    NSArray *cached = objc_getAssociatedObject(fn, &pvgmtl_embedded_plan_key);
    if (cached) return cached;
    @synchronized(fn) {
        cached = objc_getAssociatedObject(fn, &pvgmtl_embedded_plan_key);
        if (cached) return cached;
        NSMutableArray *plans = [NSMutableArray new];
        uint64_t decl[2][8]; const BOOL haveDecl = pvgmtl_spirv_declared(fn->_spirv, decl); unsigned heapRouted = 0;
        for (NSDictionary *entry in pvgmtl_embedded_bindings(fn)) {
            NSDictionary *src = entry[@"embedded_source"];
            NSNumber *slotNum = entry[@"metal_index"];
            if (![src isKindOfClass:[NSDictionary class]] || !slotNum) continue;
            PVGMTLEmbeddedPlan *plan = [PVGMTLEmbeddedPlan new];
            plan->wantSampler = [entry[@"kind"] isEqualToString:@"EmbeddedArgBufferSampler"];
            plan->storage = [entry[@"access"] isKindOfClass:[NSString class]] && [entry[@"access"] isEqualToString:@"Storage"];
            plan->slot = (uint32_t)slotNum.unsignedLongValue;
            plan->ownerIndex = [src[@"buffer_index"] unsignedLongValue];
            plan->fieldOffset = [src[@"field_offset"] unsignedLongValue];
            id binding = entry[@"descriptor"][@"binding"];
            plan->bindingText = binding ? [[binding description] copy] : @"(absent)";
            { id ds = entry[@"descriptor"][@"set"];
              plan->dset = [ds respondsToSelector:@selector(unsignedIntValue)] ? [ds unsignedIntValue] : 0;
              plan->dbinding = [binding respondsToSelector:@selector(unsignedIntValue)] ? [binding unsignedIntValue] : UINT32_MAX; }
            if (haveDecl && !plan->wantSampler && plan->dset < 2 && plan->dbinding < 512 &&
                !((decl[plan->dset][plan->dbinding >> 6] >> (plan->dbinding & 63)) & 1)) { heapRouted++; continue; }
            [plans addObject:plan];
        }
        { static _Atomic unsigned said; if (heapRouted && atomic_fetch_add(&said, 1) < 8)
            nvlog("tier2: %s reads %u argument-buffer texture(s) through the bindless heap; %lu field(s) still bound per draw",
                  fn->_fname.UTF8String, heapRouted, (unsigned long)plans.count); }
        cached = [plans copy];
        objc_setAssociatedObject(fn, &pvgmtl_embedded_plan_key, cached, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
        return cached;
    }
}

static BOOL pvgmtl_embedded_info_due(_Atomic uint64_t *counter) {
    uint64_t n = atomic_fetch_add_explicit(counter, 1, memory_order_relaxed) + 1;
    return n <= 8 || (n && !(n & (n - 1)));
}
static _Atomic uint64_t pvgmtl_embedded_empty_count, pvgmtl_embedded_bound_count;
static void pvgmtl_clear_embedded_slot(PVGMTLCommandBuffer *cb, uint32_t set, uint32_t slot, BOOL sampler) {
    if (sampler) pvgmtl_vk_bind_parked_sampler(&cb->_c, set, slot, NULL);
    else {
        pvgmtl_vk_bind_texture_view(&cb->_c, set, slot, NULL);
        pvgmtl_vk_bind_storage_view(&cb->_c, set, slot, NULL);
    }
}
void pvgmtl_bind_embedded(PVGMTLCommandBuffer *cb, uint32_t set, PVGMTLFunction *fn, PVGMTLBuffer *__strong const *bufAt, const NSUInteger *offAt)
{
    if (!cb || !fn) return;
    NSArray<NSArray *> *statics = pvgmtl_static_samplers_on() ? pvgmtl_static_samplers(fn) : nil;
    NSArray<PVGMTLEmbeddedPlan *> *embedded = pvgmtl_embedded_plan(fn);
    // give the app back any sampler slot an earlier shader parked its own sampler in and this one leaves alone
    uint32_t keep = 0;
    for (NSArray *pair in statics) { uint32_t i = [pair[0] unsignedIntValue]; if (i < 32) keep |= 1u << i; }
    for (PVGMTLEmbeddedPlan *plan in embedded) if (plan->wantSampler && plan->slot < 32) keep |= 1u << plan->slot;
    pvgmtl_vk_parked_samplers_keep(&cb->_c, set, keep);
    for (NSArray *pair in statics) {
        PVGMTLSamplerState *s = pair[1];
        pvgmtl_retain_resource(cb, s);
        if (pvgmtl_vk_bind_parked_sampler(&cb->_c, set, [pair[0] unsignedIntValue], &s->_s))
            nvlog("static sampler slot %@: bind FAILED", pair[0]);
    }
    if (!embedded.count) return;
    extern int pvgmtl_vk_emb_same(nvk_cmdbuf *, uint32_t, const void *, const uint64_t *, uint32_t);
    extern void pvgmtl_vk_emb_note(nvk_cmdbuf *, uint32_t, const void *, const uint64_t *, uint32_t);
    uint64_t embKey[480]; uint32_t embN = 0; BOOL embKeyed = embedded.count * 3 <= 480, embSurf = NO;
    if (embKeyed) {
        for (PVGMTLEmbeddedPlan *plan in embedded) {
            PVGMTLBuffer *owner = plan->ownerIndex < PVGMTL_NBUFAT ? bufAt[plan->ownerIndex] : nil;
            if (!owner) { embKeyed = NO; break; }
            NSUInteger base = offAt[plan->ownerIndex]; size_t msize = owner->_b.size;
            // pvgmtl_buf_cpu, not _b.map: a Managed buffer keeps its CPU copy in _shadow and _b.map is
            // null there, so reading the raw map refused every slot such a buffer owns. It also
            // settles the buffer first. PVGMTL_EMBEDDED_MANAGED=0 restores the old refusal (a split switch).
            const uint8_t *ocpu = (const uint8_t *)(pvgmtl_embedded_managed_on() ? pvgmtl_buf_cpu(owner) : owner->_b.map);
            if (!ocpu || base > msize || plan->fieldOffset > msize - base || sizeof(uint64_t) > msize - base - plan->fieldOffset) { embKeyed = NO; break; }
            uint64_t h = 0; memcpy(&h, ocpu + base + plan->fieldOffset, sizeof h);
            embKey[embN++] = (uint64_t)(uintptr_t)(__bridge void *)owner; embKey[embN++] = base; embKey[embN++] = h;
        }
        if (embKeyed && pvgmtl_vk_emb_same(&cb->_c, set, (__bridge void *)fn, embKey, embN)) return;
    }

    for (PVGMTLEmbeddedPlan *plan in embedded) {
        BOOL wantSampler = plan->wantSampler;
        const char *what = wantSampler ? "sampler" : "texture";
        uint32_t slot = plan->slot;
        NSUInteger ownerIndex = plan->ownerIndex;
        NSUInteger fieldOffset = plan->fieldOffset;

        PVGMTLBuffer *owner = ownerIndex < PVGMTL_NBUFAT ? bufAt[ownerIndex] : nil;
        if (!owner) {
            nvlog("embedded %s slot %u: buffer(%lu) is not bound on this encoder — NOT BOUND",
                  what, slot, (unsigned long)ownerIndex);
            pvgmtl_clear_embedded_slot(cb, set, slot, wantSampler); continue;
        }
        NSUInteger base = offAt[ownerIndex];
        size_t msize = owner->_b.size;
        // See above: a Managed buffer's CPU copy lives in _shadow, so _b.map is null and the slot was
        // refused although the handle was readable.
        const uint8_t *ocpu = (const uint8_t *)(pvgmtl_embedded_managed_on() ? pvgmtl_buf_cpu(owner) : owner->_b.map);
        if (!ocpu || base > msize || fieldOffset > msize - base ||
            sizeof(uint64_t) > msize - base - fieldOffset) {
            nvlog("embedded %s slot %u: buffer(%lu) offset %lu+%lu does not fit its %zu-byte mapping — REFUSED",
                  what, slot, (unsigned long)ownerIndex, (unsigned long)base,
                  (unsigned long)fieldOffset, owner->_b.size);
            pvgmtl_clear_embedded_slot(cb, set, slot, wantSampler); continue;
        }
        uint64_t handle = 0;
        memcpy(&handle, ocpu + base + fieldOffset, sizeof handle);
        if (!handle) {
            if (pvgmtl_embedded_info_due(&pvgmtl_embedded_empty_count))
                nvlog("embedded %s slot %u: buffer(%lu)+%lu holds a zero handle — binding cleared",
                      what, slot, (unsigned long)ownerIndex, (unsigned long)fieldOffset);
            pvgmtl_clear_embedded_slot(cb, set, slot, wantSampler); continue;
        }
        id resource = pvgmtl_resid_lookup(handle);
        if (!resource) {
            nvlog("embedded %s slot %u: handle 0x%llx resolves to nothing — released, or never ours; REFUSED",
                  what, slot, (unsigned long long)handle);
            pvgmtl_clear_embedded_slot(cb, set, slot, wantSampler); continue;
        }
        if (wantSampler != ((handle & PVGMTL_RESID_SAMPLER) != 0)) {
            nvlog("embedded slot %u: the field wants a %s but handle 0x%llx is the other kind — REFUSED",
                  slot, what, (unsigned long long)handle);
            pvgmtl_clear_embedded_slot(cb, set, slot, wantSampler); continue;
        }
        if (pvgmtl_embedded_info_due(&pvgmtl_embedded_bound_count)) {
            const char *bindStr = plan->bindingText.UTF8String;
            nvlog("embedded %s slot %u -> module binding %s (buffer(%lu)+%lu, handle 0x%llx) BOUND",
                  what, slot, bindStr,
                  (unsigned long)ownerIndex, (unsigned long)fieldOffset, (unsigned long long)handle);
        }
        if (wantSampler) {
            PVGMTLSamplerState *s = resource;
            pvgmtl_retain_resource(cb, s);
            if (pvgmtl_vk_bind_parked_sampler(&cb->_c, set, slot, &s->_s))
                nvlog("embedded sampler slot %u: bind FAILED", slot);
        } else {
            PVGMTLTexture *t = resource;
            pvgmtl_retain_resource(cb, t);
            pvgmtl_surface_in_for_cb(cb, t);
            { PVGMTLTexture *sr = t->_parent ? t->_parent : t; if (sr->_surf) embSurf = YES; }
            if (pvgmtl_vk_bind_texture_view(&cb->_c, set, slot, [t nvsampleview]))
                nvlog("embedded texture slot %u: bind FAILED", slot);
            void *sv = pvgmtl_storage_view_for(cb, t, plan->storage);
            if (sv && !t->_stex) embSurf = YES;   // a twin is copied in and out per encoder: never skip this binding
            pvgmtl_vk_bind_storage_view(&cb->_c, set, slot, sv);
        }
    }
    pvgmtl_vk_emb_note(&cb->_c, set, (embKeyed && !embSurf) ? (__bridge void *)fn : NULL, embKey, embN);
}
// the compute encoder keeps its bindings as index -> (buffer, offset, ...) arrays; the tables are built only for a
// kernel that has embedded arguments
void pvgmtl_bind_embedded_enc(PVGMTLCommandBuffer *cb, uint32_t set, PVGMTLFunction *fn, PVGMTLComputeCommandEncoder *e)
{
    PVGMTLBuffer *bufs[PVGMTL_NBUFAT] = { nil }; NSUInteger offs[PVGMTL_NBUFAT] = { 0 };
    if (e && fn && pvgmtl_embedded_plan(fn).count)
        for (NSUInteger i = 0; i < PVGMTL_NBUFAT; i++)
            if (e->_baN[i] && e->_baObj[i]) { bufs[i] = e->_baObj[i]; offs[i] = e->_baOff[i]; }
    pvgmtl_bind_embedded(cb, set, fn, bufs, offs);
}
