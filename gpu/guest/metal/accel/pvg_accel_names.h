/*
 * Pome Virtual GPU accelerator class names. Derived from NullMoth's nmx_accel_names.h.
 * Copyright (c) 2026 NullMoth Systems; changes by pome contributors.
 * SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
 *
 * nvrm-accel.cpp built with -DNMX_ACCEL -DPVG_ACCEL: the headless app-only accelerator path of the
 * AMD/Intel build, on PVGTransport. IOKit class names are global, so every class is renamed.
 */
#ifndef PVG_ACCEL_NAMES_H
#define PVG_ACCEL_NAMES_H
#define NVAccel            PVGAccel
#define NVEventMachine     PVGEventMachine
#define NVDisplayMachine   PVGDisplayMachine
#define NVTask             PVGTask
#define NVMemoryMap        PVGMemoryMap
#define IOAccelNVRMCarrier IOAccelPVGCarrier
#define NVResource         PVGResource
#define NVSysMemory        PVGSysMemory
#define NVVidMemory        PVGVidMemory
#define NVSurface          PVGSurface
#define NVDisplayPipe      PVGDisplayPipe
#define NVGLContext        PVGGLContext
#define NV2DContext        PVG2DContext
#define NVCLContext        PVGCLContext
#define NVVideoContext     PVGVideoContext
#endif
