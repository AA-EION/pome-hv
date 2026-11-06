static __thread unsigned pvgmtl_mfx_profile_depth;
static BOOL (*pvgmtl_mfx_support_original)(id, SEL, id);
static float (*pvgmtl_mfx_min_original)(id, SEL, id);
static float (*pvgmtl_mfx_max_original)(id, SEL, id);
typedef id (*pvgmtl_mfx_factory_fn)(id, SEL, id) __attribute__((ns_returns_retained));
static pvgmtl_mfx_factory_fn pvgmtl_mfx_new_original;
static BOOL pvgmtl_mfx_our_device(id device) {
    return device && [NSStringFromClass(object_getClass(device)) isEqualToString:@"PVGMTLDevice"];
}
static BOOL pvgmtl_mfx_support(id receiver, SEL selector, id device) {
    if (!pvgmtl_mfx_our_device(device)) return pvgmtl_mfx_support_original(receiver, selector, device);
    ++pvgmtl_mfx_profile_depth;
    @try { return pvgmtl_mfx_support_original(receiver, selector, device); }
    @finally { --pvgmtl_mfx_profile_depth; }
}
static float pvgmtl_mfx_min(id receiver, SEL selector, id device) {
    if (!pvgmtl_mfx_our_device(device)) return pvgmtl_mfx_min_original(receiver, selector, device);
    ++pvgmtl_mfx_profile_depth;
    @try { return pvgmtl_mfx_min_original(receiver, selector, device); }
    @finally { --pvgmtl_mfx_profile_depth; }
}
static float pvgmtl_mfx_max(id receiver, SEL selector, id device) {
    if (!pvgmtl_mfx_our_device(device)) return pvgmtl_mfx_max_original(receiver, selector, device);
    ++pvgmtl_mfx_profile_depth;
    @try { return pvgmtl_mfx_max_original(receiver, selector, device); }
    @finally { --pvgmtl_mfx_profile_depth; }
}
static id pvgmtl_mfx_new(id receiver, SEL selector, id device) __attribute__((ns_returns_retained));
static id pvgmtl_mfx_new(id receiver, SEL selector, id device) {
    if (!pvgmtl_mfx_our_device(device)) return pvgmtl_mfx_new_original(receiver, selector, device);
    ++pvgmtl_mfx_profile_depth;
    @try {
        id scaler = pvgmtl_mfx_new_original(receiver, selector, device);
        nvlog("MetalFX temporal native profile: %s", scaler ? NSStringFromClass(object_getClass(scaler)).UTF8String : "unavailable");
        return scaler;
    } @finally { --pvgmtl_mfx_profile_depth; }
}
__attribute__((constructor)) static void pvgmtl_mfx_profile_install(void) {
    // This constructor runs before the one that reads nvrm610.conf; settle the knobs first so a conf or profile value counts.
    pvgmtl_conf_settle();
    const char *enabled = getenv("PVGMTL_METALFX_NATIVE_PROFILE");
    if (!enabled || strcmp(enabled, "1")) return;
    Class type = MTLFXTemporalScalerDescriptor.class;
    Method support = class_getClassMethod(type, @selector(supportsDevice:));
    Method min = class_getClassMethod(type, @selector(supportedInputContentMinScaleForDevice:));
    Method max = class_getClassMethod(type, @selector(supportedInputContentMaxScaleForDevice:));
    Method factory = class_getInstanceMethod(type, @selector(newTemporalScalerWithDevice:));
    if (!support || !min || !max || !factory) { nvlog("MetalFX temporal native profile: API unavailable"); return; }
    pvgmtl_mfx_support_original = (void *)method_getImplementation(support);
    pvgmtl_mfx_min_original = (void *)method_getImplementation(min);
    pvgmtl_mfx_max_original = (void *)method_getImplementation(max);
    pvgmtl_mfx_new_original = (pvgmtl_mfx_factory_fn)method_getImplementation(factory);
    method_setImplementation(support, (IMP)pvgmtl_mfx_support);
    method_setImplementation(min, (IMP)pvgmtl_mfx_min);
    method_setImplementation(max, (IMP)pvgmtl_mfx_max);
    method_setImplementation(factory, (IMP)pvgmtl_mfx_new);
    nvlog("MetalFX temporal native profile: installed for PVGMTL devices");
}
