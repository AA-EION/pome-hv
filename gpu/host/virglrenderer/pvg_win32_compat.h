/*
 * POSIX pieces virglrenderer's Venus path uses that MinGW-w64 does not provide.
 * Copyright 2026 pome contributors. SPDX-License-Identifier: MIT
 * Force-included on Windows builds (-include pvg_win32_compat.h).
 */
#ifndef PVG_WIN32_COMPAT_H
#define PVG_WIN32_COMPAT_H
#ifdef _WIN32
#include <stddef.h>
#include <stdint.h>
#include <io.h>
#include <errno.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#ifndef HAVE_GETPAGESIZE_DECL
static inline int pvg_getpagesize(void) { SYSTEM_INFO si; GetSystemInfo(&si); return (int)si.dwPageSize; }
#define getpagesize pvg_getpagesize
#endif

#ifndef O_CLOEXEC
#define O_CLOEXEC 0
#endif
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif


#endif /* _WIN32 */
#endif
