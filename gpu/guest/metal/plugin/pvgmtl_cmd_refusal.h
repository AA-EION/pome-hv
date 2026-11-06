/*
 * NullMoth NVIDIA driver for macOS
 * Copyright (c) 2026 NullMoth Systems.
 * SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
 */
#ifndef PVGMTL_CMD_REFUSAL_H
#define PVGMTL_CMD_REFUSAL_H
/*
 * A refused command buffer: why it was refused.
 *
 * -[MTLCommandQueue commandBuffer] returns nil when the Vulkan side cannot begin a command buffer. WindowServer
 * does not tolerate that: CoreDisplay asserts "Failed to get a MTLCommandBuffer" and the session restarts. The cause
 * is usually an earlier failure that disabled the backend for the rest of the process, so the line that names the
 * refusal says which case it is. The texts are macros so that each cause is logged from its own call site: nvlog
 * throttles by format string, and one shared format would let a flood of one cause hide the first line of another.
 */
typedef enum {
    PVGMTL_REFUSE_NONE = 0,
    PVGMTL_REFUSE_BACKEND_DOWN,   /* the backend is not running in this process */
    PVGMTL_REFUSE_POOL,           /* vkCreateCommandPool failed */
    PVGMTL_REFUSE_ALLOC,          /* vkAllocateCommandBuffers failed */
    PVGMTL_REFUSE_BEGIN           /* vkBeginCommandBuffer failed */
} pvgmtl_refusal;

#define PVGMTL_REFUSE_TEXT_BACKEND_DOWN \
    "the Vulkan backend is not running in this process: it failed to start, or a submit, a fence wait or the " \
    "completion check disabled it (look for 'disabling backend' or a start-up failure above)"
#define PVGMTL_REFUSE_TEXT_POOL  "vkCreateCommandPool failed"
#define PVGMTL_REFUSE_TEXT_ALLOC "vkAllocateCommandBuffers failed"
#define PVGMTL_REFUSE_TEXT_BEGIN "vkBeginCommandBuffer failed"

static inline const char *pvgmtl_refusal_text(pvgmtl_refusal why)
{
    switch (why) {
    case PVGMTL_REFUSE_BACKEND_DOWN: return PVGMTL_REFUSE_TEXT_BACKEND_DOWN;
    case PVGMTL_REFUSE_POOL:         return PVGMTL_REFUSE_TEXT_POOL;
    case PVGMTL_REFUSE_ALLOC:        return PVGMTL_REFUSE_TEXT_ALLOC;
    case PVGMTL_REFUSE_BEGIN:        return PVGMTL_REFUSE_TEXT_BEGIN;
    default:                        return "no refusal";
    }
}
#endif
