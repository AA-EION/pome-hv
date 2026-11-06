/* pome: ring-thread priority on Windows. SPDX-License-Identifier: MIT */
#pragma once
#include <windows.h>
#define PRIO_PROCESS 0
static inline int setpriority(int which, int who, int prio)
{
   (void)which; (void)who;
   const int p = prio < -10 ? THREAD_PRIORITY_HIGHEST : prio < 0 ? THREAD_PRIORITY_ABOVE_NORMAL
               : prio > 10 ? THREAD_PRIORITY_LOWEST : prio > 0 ? THREAD_PRIORITY_BELOW_NORMAL : THREAD_PRIORITY_NORMAL;
   return SetThreadPriority(GetCurrentThread(), p) ? 0 : -1;
}
