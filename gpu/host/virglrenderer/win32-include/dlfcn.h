/* pome: dlopen for vkr on Windows. Any Vulkan loader name resolves to the system vulkan-1.dll.
 * SPDX-License-Identifier: MIT */
#pragma once
#include <string.h>
#include <windows.h>
#define RTLD_NOW 2
#define RTLD_LOCAL 0
#define RTLD_LAZY 1
static char pvg_dlerror_buf[64];
static inline void *dlopen(const char *name, int flags)
{
   (void)flags;
   HMODULE m = NULL;
   if (name && strstr(name, "vulkan"))
      m = LoadLibraryExA("vulkan-1.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
   else if (name)
      m = LoadLibraryA(name);
   if (!m)
      snprintf(pvg_dlerror_buf, sizeof(pvg_dlerror_buf), "LoadLibrary failed (%lu)", GetLastError());
   return (void *)m;
}
static inline void *dlsym(void *handle, const char *symbol)
{
   FARPROC f = GetProcAddress((HMODULE)handle, symbol);
   void *p;
   memcpy(&p, &f, sizeof(p)); /* ISO C has no function-to-object pointer cast */
   return p;
}
static inline int dlclose(void *handle) { return FreeLibrary((HMODULE)handle) ? 0 : -1; }
static inline char *dlerror(void) { return pvg_dlerror_buf[0] ? pvg_dlerror_buf : NULL; }
