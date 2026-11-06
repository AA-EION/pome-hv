/*
 * NullMoth NVIDIA driver for macOS
 * Copyright (c) 2026 NullMoth Systems.
 * SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
 */

#pragma once
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <IOSurface/IOSurface.h>
#include "pvgmtl_vk.h"
#include "pvgmtl_cmd_refusal.h"

static inline NSMutableData *pvgmtl_fc_storage(NSUInteger count) {
    const NSUInteger stride = 2 * sizeof(uint32_t) + sizeof(void *) + 16;
    if (!count || count > NSUIntegerMax / stride) return nil;
    return [NSMutableData dataWithLength:count * stride];
}
@class PVGMTLHeap;
@interface PVGMTLBuffer : NSObject <MTLBuffer> { @public nvk_buffer _b; MTLStorageMode _storage; PVGMTLHeap *_heap; NSUInteger _heapOffset;
    void *_hostPtr; size_t _hostLen; id _hostDealloc;
    PVGMTLHeap *_subHeap; NSUInteger _subCharge; BOOL _aliased;
    nvk_buffer _shadow; int _syncOut; int _zshadow;
    NSUInteger _dirtyLo, _dirtyHi; } @end
#include <sched.h>
static inline void pvgmtl_buf_settle(PVGMTLBuffer *b) {
    if (!__atomic_load_n(&b->_zshadow, __ATOMIC_ACQUIRE)) return;
    int owed = 1;
    if (__atomic_compare_exchange_n(&b->_zshadow, &owed, 2, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
        if (b->_shadow.map) memset(b->_shadow.map, 0, b->_shadow.size);
        __atomic_store_n(&b->_zshadow, 0, __ATOMIC_RELEASE); return; }
    while (__atomic_load_n(&b->_zshadow, __ATOMIC_ACQUIRE) == 2) sched_yield();
}
static inline void *pvgmtl_buf_cpu(PVGMTLBuffer *b) { pvgmtl_buf_settle(b); return b->_shadow.map ? b->_shadow.map : b->_b.map; }
@interface PVGMTLTexture : NSObject <MTLTexture> {
  @public nvk_image _i; nvk_queue *_q; IOSurfaceRef _surf; MTLPixelFormat _fmt; NSUInteger _plane;
  PVGMTLHeap *_heap; NSUInteger _heapOffset;
  PVGMTLHeap *_subHeap; NSUInteger _subCharge;
  BOOL _aliased;
  PVGMTLTexture *_parent;
  void *_sampledDepthView;
  void *_ownView;
  uint32_t _mips;
  PVGMTLTexture *_viewParent;
  uint32_t _viewType, _viewSlices, _relativeLevel, _relativeSlice;
  uint32_t _baseSlice, _baseLevel;
  uint32_t _surfSeed, _surfGen; BOOL _surfSynced, _surfOwned;
  nvk_buffer _vramBuf; BOOL _vramOn;
  nvk_buffer _hostPages;   // this surface's pages imported for the host copies (pvgmtl_host_pages), kept while they hold
  BOOL _stex;
  uint64_t _resid;
  NSUInteger _usage;
  PVGMTLBuffer *_backBuf; NSUInteger _backOff, _backBPR;
}
- (nvk_image *)nvi;
- (void *)nvview;
- (void *)nvsampleview;
- (void)pvgmtlSurfaceIn;
- (BOOL)pvgmtlSurfaceOut;
- (BOOL)pvgmtlSurfaceOutDirtied:(BOOL)dirtied;
@end
@interface PVGMTLFunction : NSObject <MTLFunction> { @public NSData *_spirv; NSString *_fname; NSString *_stage; NSString *_air; NSDictionary *_fc; NSArray *_embedded; unsigned char _bcHash[32]; BOOL _bcHashed;
  NSDictionary *_refl; NSDictionary *_airNames; NSArray *_arguments;
  id _lib; id _fcv; NSString *_specName; BOOL _needsLink; } @end
NSDictionary *pvgmtl_function_reflection(PVGMTLFunction *function);
NSDictionary *pvgmtl_air_names(PVGMTLFunction *function);
uint64_t pvgmtl_resid_for_texture(id t);
void pvgmtl_resid_texture_gone(uint64_t token);
uint64_t pvgmtl_resid_for_sampler(id s);
@interface PVGMTLType : NSObject { @public MTLDataType _dataType; } @end
@interface PVGMTLVertexAttribute : NSObject { @public NSString *_name; NSUInteger _index; MTLDataType _dataType; } @end
@interface PVGMTLTextureReferenceType : PVGMTLType { @public MTLDataType _texDataType; MTLTextureType _texType; MTLBindingAccess _access; BOOL _depth; } @end
@interface PVGMTLStructType : PVGMTLType { @public NSArray *_members; } @end
@interface PVGMTLPointerType : PVGMTLType { @public MTLDataType _elem; MTLBindingAccess _access; NSUInteger _align, _size; BOOL _isArgBuf; PVGMTLStructType *_elemStruct; } @end
// an array member of a reflected struct (MTLArrayType): element type, length and stride from the AIR struct_type_info
@interface PVGMTLArrayType : PVGMTLType { @public MTLDataType _elem; NSUInteger _len, _stride; } @end
@interface PVGMTLStructMember : NSObject { @public NSString *_name; NSUInteger _offset; MTLDataType _dataType; NSUInteger _argIndex; PVGMTLArrayType *_arr;
  PVGMTLStructType *_struct; PVGMTLTextureReferenceType *_texRef; PVGMTLPointerType *_ptr; } @end
@interface PVGMTLArgument : NSObject { @public NSString *_name; MTLArgumentType _type; MTLBindingAccess _access; NSUInteger _index; BOOL _active;
  NSUInteger _arrayLength, _bufAlign, _bufSize; MTLDataType _bufDataType; PVGMTLStructType *_bufStruct; PVGMTLPointerType *_bufPtr;
  MTLTextureType _texType; MTLDataType _texDataType; BOOL _depth; } @end
@interface PVGMTLRenderPipelineReflection : NSObject { @public NSArray *_v, *_f, *_o, *_m; } @end
@interface PVGMTLComputePipelineReflection : NSObject { @public NSArray *_a; } @end
NSArray<PVGMTLArgument *> *pvgmtl_arguments_for_function(PVGMTLFunction *fn);
PVGMTLArgument *pvgmtl_argument_for_buffer(PVGMTLFunction *fn, NSUInteger index);
id pvgmtl_render_reflection(MTLRenderPipelineDescriptor *d);
id pvgmtl_compute_reflection(id<MTLFunction> fn);
id<MTLFunction> pvgmtl_link_kernel(MTLComputePipelineDescriptor *d, NSError **err);
id<MTLFunction> pvgmtl_link_stage(id<MTLFunction> k, MTLLinkedFunctions *lf, NSArray *dylibs, NSError **err);
void pvgmtl_render_keep_linked(id ps, id<MTLFunction> vf, id<MTLFunction> ff);
void pvgmtl_note_compile_dylibs(id lib, NSArray *libraries);
id pvgmtl_new_dylib(id<MTLLibrary> lib, NSError **err);
id pvgmtl_new_dylib_url(NSURL *url, NSError **err);
id pvgmtl_new_binary_archive(MTLBinaryArchiveDescriptor *d, NSError **err);
NSString *pvgmtl_archive_refusal(NSArray *archives, NSString *key);
NSString *pvgmtl_archive_key_compute(id<MTLFunction> f);
NSString *pvgmtl_archive_key_render(MTLRenderPipelineDescriptor *d);
static inline uint32_t pvgmtl_vk_layers_for_desc(uint32_t t, uint32_t n, uint32_t depth) { return t == 7 ? (depth ? depth : 1u) : pvgmtl_vk_layers_for_type(t, n); }

@interface PVGMTLArgumentEncoder : NSObject <MTLArgumentEncoder> {
  @public NSArray<NSDictionary *> *_fields; NSDictionary *_fieldByIndex;
  int64_t *_offAt; NSUInteger _offN;
  PVGMTLBuffer *_dst; NSUInteger _dstOffset; NSString *_label;
  NSUInteger _airSize, _airAlign;
} @end
extern id gPVGMTLMainDevice;
@interface PVGMTLLibrary : NSObject <MTLLibrary> { @public NSDictionary<NSString *, NSData *> *_fns; NSMutableDictionary<NSString *, NSData *> *_compiledFns; NSDictionary<NSString *, NSString *> *_stages, *_airs; NSArray<NSString *> *_externs;
  NSData *_raw; id _appleTwin; BOOL _twinTried; NSString *_rawPath; } @end
@interface PVGMTLRenderPipelineState : NSObject <MTLRenderPipelineState> { @public nvk_pipeline _p;
  NSString *_vname, *_fname, *_blend, *_vdesc; MTLRenderPipelineDescriptor *_descriptor; NSMutableDictionary *_samplerVariants;
  PVGMTLFunction *_linkedVert, *_linkedFrag; uint32_t _a8mask; } @end
@interface PVGMTLCommandQueue : NSObject <MTLCommandQueue> {
  // _resSets: the residency sets this queue was given. A set retains its allocations, so holding the
  // set is what keeps them alive; without this nobody did, and an app that handed its only reference
  // to the queue had every resource in the set freed under work the queue had not finished.
  @public nvk_queue _q; id _dev; id _completionQueue; dispatch_queue_t _callbackOrderQueue; NSMutableArray *_reservations; dispatch_semaphore_t _capacity; NSUInteger _capacityCount; NSMutableArray *_resSets;
} @end
@interface PVGMTLCommandBuffer : NSObject <MTLCommandBuffer> {
  NSError *_cbError;
  NSMutableDictionary *_userDictionary;
  @public nvk_cmdbuf _c; PVGMTLCommandQueue *_queue; BOOL _committed, _commitMarking, _partsRetiring; BOOL _capacityHeld; MTLCommandBufferStatus _cbstatus;
  PVGMTLTexture *_present;
  NSMutableArray *_presentations;
  NSMutableArray *_handlers;
  NSMutableArray *_scratch;
  NSMutableSet *_resources;
  NSMutableSet *_surfDirty;
  NSMutableSet *_surfOverwrites; // contents defined by writes recorded in this command buffer
  unsigned _nEnc, _nEnd, _nHandlers, _nStatus, _nEnqueue, _nLabel, _nPresent, _nWait;
  unsigned _nEncSealed;
  CFTimeInterval _gpuStart, _gpuEnd;
  CFTimeInterval _legacyKernelStart, _legacyKernelEnd;
  uint64_t _cbtSeq, _cbtCreate, _cbtCommit, _cbtWait0; unsigned _cbtND; char _cbtK[40];   // PVGMTL_CB_TIME (dev)
  pvgmtl_gpu_interval _gpuTiming; BOOL _gpuTimingSeen, _gpuTimingInvalid;
  NSMutableArray *_parts, *_scheduledHandlers;
  NSUInteger _nextPart;
  NSUInteger _nextSubmit;
  id _reservation;
  NSCondition *_completionCondition;
  dispatch_group_t _callbackGroup, _scheduledGroup;
  BOOL _wasScheduled, _executionDone, _completionReady, _scheduleAfterPublish;
  NSMutableArray *_signals;
  NSMutableArray *_waits;
}
- (void)pvgmtlReserveQueue;
- (void)pvgmtlReleaseReservation;
- (void)pvgmtlRecordWait:(id)event value:(uint64_t)value;
- (void)pvgmtlRecordSignal:(id)event value:(uint64_t)value;
- (void)pvgmtlSubmitWithEvents;
- (BOOL)pvgmtlAdvancePart;
- (BOOL)pvgmtlSubmitAhead;
- (void)pvgmtlReleaseCapacity;
- (void)pvgmtlRecordEncodingError:(NSError *)error;
- (void)pvgmtlFinishCallbacks;
@end
@interface PVGMTLDepthStencilState : NSObject <MTLDepthStencilState> { @public int _write; int _compare;
  int _stencil; uint32_t _sf[6], _sb[6]; } @end
@interface PVGMTLSamplerState : NSObject <MTLSamplerState> { @public nvk_sampler _s; NSDictionary *_pixelState; uint64_t _resid; } @end
@interface PVGMTLFence : NSObject <MTLFence> { @public id<MTLDevice> _dev; NSString *_label; } @end
@interface PVGMTLResidencySet : NSObject <MTLResidencySet> { @public id<MTLDevice> _dev; NSString *_label; NSMutableArray *_allocs; } @end
@interface PVGMTLEvent : NSObject <MTLEvent> { @public id<MTLDevice> _dev; NSString *_label; uint64_t _value; NSCondition *_condition; } @end
@interface PVGMTLSharedEvent : NSObject <MTLSharedEvent> { @public id<MTLDevice> _dev; NSString *_label; uint64_t _value; NSCondition *_condition; NSMutableArray *_notifications; } @end
@interface PVGMTLHeap : NSObject <MTLHeap> {
  @public id<MTLDevice> _dev; NSUInteger _size; NSUInteger _used;
  MTLStorageMode _storage; MTLCPUCacheMode _cache; NSString *_label;
  nvk_heap _hm; MTLHeapType _type;
}
- (void)pvgmtlReleaseSubAllocation:(NSUInteger)bytes;
- (void)pvgmtlGiveRange:(NSUInteger)off length:(NSUInteger)len;
@end
@interface PVGMTLComputePipelineState : NSObject <MTLComputePipelineState> { @public nvk_pipeline _p; PVGMTLFunction *_function; NSMutableDictionary *_samplerVariants;
    BOOL _icb;
} @end
@interface PVGMTLIndirectCommand : NSObject <MTLIndirectRenderCommand, MTLIndirectComputeCommand> {
  @public id _ps; NSMutableArray *_vbufs; NSMutableArray *_fbufs;
  MTLPrimitiveType _type; NSUInteger _start, _count, _instances, _baseInstance;
  id _ibuf; NSUInteger _ioff; MTLIndexType _itype; NSInteger _baseVertex;
  BOOL _indexed, _armed;
  MTLCullMode _cull; BOOL _hasCull;
  MTLWinding _winding; BOOL _hasWinding;
  MTLTriangleFillMode _fill; BOOL _hasFill;
  MTLDepthClipMode _clip; BOOL _hasClip;
  id _ds; float _bias, _slope, _clamp; BOOL _hasBias;
  id _cps; NSMutableArray *_kbufs; MTLSize _grid, _tg; BOOL _isCompute;
} @end
@interface PVGMTLIndirectCommandBuffer : NSObject <MTLIndirectCommandBuffer> {
  @public NSMutableArray<PVGMTLIndirectCommand *> *_cmds; id<MTLDevice> _dev; NSString *_label;
} @end
#ifndef PVGMTL_NBUFAT
#define PVGMTL_NBUFAT 32   /* buffer argument indices an encoder tracks (Metal has 31) */
#endif
@interface PVGMTLComputeCommandEncoder : NSObject <MTLComputeCommandEncoder> {
  @public PVGMTLCommandBuffer *_cb; PVGMTLComputePipelineState *_ps; NSMutableArray *_scratch; NSMutableDictionary *_samplers;
  NSMutableDictionary *_tgLen;
  // The binding table, by Metal buffer index: the buffer, its offset and, for setBytes, the inline length and base.
  // Plain arrays: WAS an NSMutableDictionary of NSArrays, three allocations on every setBuffer (a large part of the
  // ~2.5 us of host time per dispatch against Apple's ~0.3 us). _baN: 0 unbound, 2 = buffer+offset, 4 = setBytes form.
  __strong id _baObj[PVGMTL_NBUFAT]; NSUInteger _baOff[PVGMTL_NBUFAT], _baLen[PVGMTL_NBUFAT], _baBase[PVGMTL_NBUFAT];
  uint8_t _baN[PVGMTL_NBUFAT];
  NSMutableSet *_resources;
  NSMutableDictionary *_sassTextures, *_sassSamplers;   // by Metal index, for kernels run from machine code
} @end
@interface PVGMTLBlitCommandEncoder : NSObject <MTLBlitCommandEncoder> { @public PVGMTLCommandBuffer *_cb; __weak PVGMTLCommandBuffer *_owner; } @end
@interface PVGMTLAccelerationStructure : PVGMTLBuffer <MTLAccelerationStructure> { @public nvk_accel _as; nvk_buffer _uid; nvk_buffer _scratch; uint32_t _iftAdd; } @end
@interface PVGMTLAccelerationStructureCommandEncoder : NSObject <MTLAccelerationStructureCommandEncoder> { @public PVGMTLCommandBuffer *_cb; __weak PVGMTLCommandBuffer *_owner; } @end
uint32_t pvgmtl_geoms_from_descriptor(id desc, pvgmtl_geom *out, uint32_t max, int *instance, uint32_t *instance_count, NSMutableSet *track);
#define PVGMTL_NBUFAT 32   /* buffer argument indices an encoder tracks (Metal has 31) */
@interface PVGMTLRenderCommandEncoder : NSObject <MTLRenderCommandEncoder> {
  @public PVGMTLCommandBuffer *_cb; PVGMTLTexture *_target; float _clear[4]; BOOL _begun;
  PVGMTLTexture *_resolve; uint32_t _passSamples;
  int _cull; uint32_t _passFmt; int _passDepth;
  uint32_t _load; BOOL _hasVP, _hasSC; float _vp[6]; uint32_t _sc[4];
  NSMutableDictionary *_vertexSamplers, *_fragmentSamplers;
  NSData *_pixelSamplerKey; // sampler specialization depends on values, not the draw or pipeline
  NSMutableArray *_pixelSamplerKeys; // at most eight recent value snapshots, owned by this encoder
  // the buffer and offset bound at each vertex/fragment index, read by embedded argument binding; plain arrays because
  // Apple's GL rebinds buffers for every draw, and a dictionary of (buffer, offset) arrays allocated on each one
  PVGMTLBuffer *_vbufAt[PVGMTL_NBUFAT], *_fbufAt[PVGMTL_NBUFAT]; NSUInteger _vbufOff[PVGMTL_NBUFAT], _fbufOff[PVGMTL_NBUFAT];
  PVGMTLRenderPipelineState *_ps;
  NSMutableArray *_scratch;
  PVGMTLDepthStencilState *_ds;
  PVGMTLBuffer *_tfb; NSUInteger _tfbOff, _tfbStride; float _tfScale; BOOL _tfScaleSet;
  PVGMTLBuffer *_visBuf;
  uint32_t _visSlot; NSUInteger _visOffset;
  BOOL _visActive, _visPending;
  NSMutableArray *_visRecords;
  nvk_pass _pass; nvk_rtsig _passSig; BOOL _hasRef, _refused;
} @end
@interface PVGMTLRenderCommandEncoder ()
- (BOOL)pvgmtlBuildPass:(MTLRenderPassDescriptor *)d;
- (BOOL)pvgmtlBeginPassLoad:(uint32_t)load;
- (void)pvgmtlApplyDepthStencil;
@end

NSArray<NSDictionary *> *pvgmtl_embedded_bindings(PVGMTLFunction *function);
void pvgmtl_bind_embedded(PVGMTLCommandBuffer *cb, uint32_t set, PVGMTLFunction *fn, PVGMTLBuffer *__strong const *bufAt, const NSUInteger *offAt);
void pvgmtl_bind_embedded_enc(PVGMTLCommandBuffer *cb, uint32_t set, PVGMTLFunction *fn, PVGMTLComputeCommandEncoder *e);
// the NSArray form of one binding ({buffer, offset} or {buffer, offset, length, base}), for the few readers that want it
static inline NSArray *pvgmtl_bufat(PVGMTLComputeCommandEncoder *e, NSUInteger i) {
    if (!e || i >= PVGMTL_NBUFAT || !e->_baN[i] || !e->_baObj[i]) return nil;
    return e->_baN[i] == 4 ? @[e->_baObj[i], @(e->_baOff[i]), @(e->_baLen[i]), @(e->_baBase[i])] : @[e->_baObj[i], @(e->_baOff[i])];
}
uint64_t pvgmtl_resid_for_texture(id t);
uint64_t pvgmtl_resid_for_sampler(id s);

@interface PVGMTLParallelRenderCommandEncoder : NSObject <MTLParallelRenderCommandEncoder> { @public PVGMTLCommandBuffer *_cb; MTLRenderPassDescriptor *_desc;
  NSMutableArray *_subs; NSLock *_lock; BOOL _ended; NSString *_label; } @end

@interface PVGMTLCounter : NSObject <MTLCounter> @end
@interface PVGMTLCounterSet : NSObject <MTLCounterSet> { @public NSArray *_counters; } @end
@interface PVGMTLCounterSampleBuffer : NSObject <MTLCounterSampleBuffer> {
  @public void *_pool; NSUInteger _count; MTLStorageMode _storage; NSString *_label; id<MTLDevice> _dev; } @end
id<MTLCounterSet> pvgmtl_timestamp_counter_set(void);

@interface PVGMTLBuffer () { @public NSString *_label; MTLResourceOptions _ropt;
  int _resRefs, _rstate, _lruReg, _wantOn; uint64_t _lastUse;
  uint64_t _useEp;
  BOOL _texSeen;
} @end
@interface PVGMTLTexture () { @public NSString *_label; MTLResourceOptions _ropt; BOOL _shareable; BOOL _fbo;
  MTLTextureSwizzleChannels _swz; BOOL _hasSwz; void *_swzView;
  void *_tbView, *_tbAlias; BOOL _tbStorage;
  int _resRefs;
  int _nviews;
  int _rstate;
  uint64_t _lastUse;
  int _lruReg, _wantOn;
  uint64_t _useEp;
} @end
void pvgmtl_texture_desc_swizzle(PVGMTLTexture *t, MTLTextureDescriptor *d);
#include <os/lock.h>
@interface PVGMTLResSet : NSMutableSet { @public NSMutableSet *_s; os_unfair_lock _lk; const void *_mru[8]; unsigned _mruNext; }
- (PVGMTLResSet *)pvgmtlTakeAll;
@end
@interface PVGMTLFunction () { @public NSString *_label; } @end
// machine code compiled ahead of time for this function (PVGMTLSass.m), nil otherwise
@interface PVGMTLFunction () { @public id _sassKernel; } @end
@interface PVGMTLLibrary () { @public NSString *_label; } @end
@interface PVGMTLCommandQueue () { @public NSString *_label; } @end
@interface PVGMTLCommandBuffer () { @public NSString *_label; uint64_t _rep; } @end
@interface PVGMTLCommandBuffer () { @public NSMutableSet *_stTwins; } @end
@interface PVGMTLTexture () { @public nvk_image _stTwin; BOOL _stTwinTried; BOOL _mipsWritten; } @end
@interface PVGMTLDepthStencilState () { @public NSString *_label; } @end
@interface PVGMTLSamplerState () { @public NSString *_label; } @end
@interface PVGMTLComputePipelineState () { @public NSString *_label; } @end
@interface PVGMTLComputePipelineState () { @public uint64_t _resid; NSUInteger _attrT, _askT; } @end
// _sassKernel: machine code that runs before the Vulkan pipeline; _sassOnly: there is no Vulkan pipeline
@interface PVGMTLComputePipelineState () { @public id _sassKernel; BOOL _sassOnly; } @end
@interface PVGMTLRenderPipelineState () { @public uint64_t _resid; } @end
// macOS 26 required threadgroup sizes, as the pipeline descriptor gave them (0x0x0 = not set). Apps read them back from
// the pipeline; without the getters the read is an unrecognized selector and the app stops.
@interface PVGMTLRenderPipelineState () { @public MTLSize _reqObj, _reqMesh; } @end
@interface PVGMTLComputePipelineState () { @public MTLSize _reqTG; } @end
// A descriptor's MTLSize property that exists only on macOS 26 and later: read by key so the plugin, built for macOS 15,
// needs no availability check and gets 0x0x0 on an older system that has no such property.
static inline MTLSize pvgmtl_desc_size(id d, SEL sel) {
    MTLSize s = { 0, 0, 0 };
    if (!d || ![d respondsToSelector:sel]) return s;
    id v = [d valueForKey:NSStringFromSelector(sel)];
    if ([v isKindOfClass:[NSValue class]] && !strcmp([(NSValue *)v objCType], @encode(MTLSize))) [(NSValue *)v getValue:&s size:sizeof s];
    return s;
}
@interface PVGMTLIndirectCommandBuffer () { @public uint64_t _resid; } @end
@interface PVGMTLIndirectCommand () - (void)pvgmtlCopyFrom:(PVGMTLIndirectCommand *)o; @end
NSUInteger pvgmtl_air_max_tg(PVGMTLFunction *fn);
BOOL pvgmtl_air_grid_only(PVGMTLFunction *fn);
// _gridOnly: 0 not yet asked, 1 the kernel's result does not depend on its threadgroup shape, -1 it may
@interface PVGMTLComputePipelineState () { @public int8_t _gridOnly; } @end
@interface PVGMTLRenderCommandEncoder () { @public NSString *_label; } @end
@interface PVGMTLRenderCommandEncoder () { @public BOOL _dbOn; float _dbLo, _dbHi; } @end   // setDepthTestMinBound:maxBound:
@interface PVGMTLRenderCommandEncoder () { @public uint32_t _vstride[PVGMTL_NVIN]; uint32_t _vstrideSet; } @end
@interface PVGMTLComputeCommandEncoder () { @public NSString *_label; } @end
@interface PVGMTLBlitCommandEncoder () { @public NSString *_label; } @end
@interface PVGMTLAccelerationStructureCommandEncoder () { @public NSString *_label; } @end
@interface PVGMTLHeap () { @public MTLHazardTrackingMode _hazard; } @end
// hazard model (pvgmtl_hazard.h): which buffer slots and textures each stage of a pipeline writes, from its reflection
@interface PVGMTLRenderPipelineState () { @public int _hzReady; uint32_t _hzBuf[2]; uint64_t _hzTex[2][2]; } @end
@interface PVGMTLComputePipelineState () { @public int _hzReady; uint32_t _hzBuf; uint64_t _hzTex[2]; } @end
@interface PVGMTLIndirectCommandBuffer () { @public MTLResourceOptions _opts; } @end
@interface PVGMTLBuffer () { @public uint32_t _purge; } @end
@interface PVGMTLTexture () { @public uint32_t _purge; } @end
@interface PVGMTLTexture () { @public BOOL _rbSwap; } @end   // a writable BGR10A2Unorm held as R10G10B10A2 (PVGMTLDevice.m)
@interface PVGMTLHeap () { @public uint32_t _purge; } @end
#include <os/lock.h>
@interface PVGMTLHeap () { @public NSUInteger *_fr; uint32_t _nfr, _cfr; os_unfair_lock _fl; BOOL _backed; } @end
@interface PVGMTLBuffer () { @public BOOL _subPlaced; } @end
typedef struct { nvk_image img; uint32_t key[12]; NSUInteger off, len; } pvgmtl_park_t;
@interface PVGMTLTexture () { @public BOOL _subPlaced, _parkable; uint32_t _pkey[12]; NSUInteger _pkLen; } @end
@interface PVGMTLTexture () { @public BOOL _clientWritten; } @end
@interface PVGMTLCommandBuffer () { @public NSDictionary *_busyMarks; } @end
@interface PVGMTLHeap () { @public pvgmtl_park_t *_pk; uint32_t _npk; } @end
@interface PVGMTLBuffer () { @public pvgmtl_park_t *_bpk; uint32_t _nbpk; os_unfair_lock _bpl; } @end
@interface PVGMTLTexture () { @public BOOL _bparkable; } @end
static void pvgmtl_heap_park(PVGMTLHeap *h, nvk_image *img, const uint32_t *key, NSUInteger off, NSUInteger len);
void pvgmtl_heap_back(PVGMTLHeap *h, NSUInteger size, int shared);
@interface PVGMTLIndirectCommandBuffer () { @public uint32_t _purge; } @end
static inline MTLPurgeableState pvgmtl_purge(uint32_t *st, MTLPurgeableState s) {
    if (s < MTLPurgeableStateKeepCurrent || s > MTLPurgeableStateEmpty) {
        static int said; if (!said++) nvlog("setPurgeableState: %lu is no MTLPurgeableState - answered as KeepCurrent", (unsigned long)s);
        s = MTLPurgeableStateKeepCurrent;
    }
    uint32_t prev = s == MTLPurgeableStateKeepCurrent ? __atomic_load_n(st, __ATOMIC_ACQUIRE) : __atomic_exchange_n(st, (uint32_t)s, __ATOMIC_ACQ_REL);
    return prev ? (MTLPurgeableState)prev : MTLPurgeableStateNonVolatile;
}
static inline kern_return_t pvgmtl_owner_check(task_id_token_t tok) {
    mach_port_t p = MACH_PORT_NULL;
    if (task_identity_token_get_task_port(tok, TASK_FLAVOR_NAME, &p) != KERN_SUCCESS) return kIOReturnBadArgument;
    mach_port_deallocate(mach_task_self(), p);
    return KERN_SUCCESS;
}
static inline MTLCPUCacheMode pvgmtl_res_cache(PVGMTLHeap *hp, MTLResourceOptions o) { return hp ? hp->_cache : (MTLCPUCacheMode)(o & 0xF); }
static inline MTLHazardTrackingMode pvgmtl_res_hazard(PVGMTLHeap *hp, MTLResourceOptions o) {
    if (hp) return hp->_hazard == MTLHazardTrackingModeTracked ? MTLHazardTrackingModeTracked : MTLHazardTrackingModeUntracked;
    return ((o >> MTLResourceHazardTrackingModeShift) & 0x3) == MTLHazardTrackingModeUntracked ? MTLHazardTrackingModeUntracked : MTLHazardTrackingModeTracked;
}
extern unsigned pvgmtl_apps1_resolve_miss, pvgmtl_apps1_resolve_extra, pvgmtl_apps1_ds_resolve_miss;
@interface MTLSharedTextureHandle (PVGMTLSPI)
- (instancetype)initWithIOSurface:(IOSurfaceRef)surface label:(NSString *)label;
- (IOSurfaceRef)ioSurface;
@end
@interface MTLIOAccelDevice (PVGMTLApps1)
- (id<MTLTexture>)newSharedTextureWithHandle:(MTLSharedTextureHandle *)handle;
@end

NSDictionary *pvgmtl_index_argument_fields(NSArray *fields);
