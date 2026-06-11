#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

// ==========================================
// 1. RazorForge Primitives (Fixed Width)
// ==========================================
// These MUST match exact bit widths.
typedef int8_t rf_S8;
typedef int16_t rf_S16;
typedef int32_t rf_S32;
typedef int64_t rf_S64;
typedef __int128 rf_S128;

typedef uint8_t rf_U8;
typedef uint16_t rf_U16;
typedef uint32_t rf_U32;
typedef uint64_t rf_U64;
typedef unsigned __int128 rf_U128;

// ==========================================
// 2. System & Data Types
// ==========================================
typedef intptr_t rf_SAddr;   // Signed Pointer-Sized
typedef uintptr_t rf_address;  // Unsigned Pointer-Sized

typedef uint8_t rf_Byte;      // 'Byte' is raw 8-bit data
typedef uint32_t rf_Character;   // 'Character' is 32-bit Unicode codepoint

typedef bool rf_Bool;         // Standard boolean

// ==========================================
// 3. C Interop Types (Platform Dependent)
// ==========================================
// These map to the C compiler's native types.
#define rf_CNull    NULL

typedef char rf_CChar;              // Ambiguous sign
typedef char* rf_CStr;

typedef int rf_CInt;
typedef unsigned int rf_CUInt;

typedef long rf_CLong;              // 32-bit on Win64, 64-bit on Linux64
typedef unsigned long rf_CULong;

typedef wchar_t rf_CWChar;

// ==========================================
// 4. The Raw Pointer Wrapper
// ==========================================
// Since Snatched<T> is just a raw pointer at runtime:
#define rf_Snatched(T) T*

// Variant (tagged union)
typedef struct rf_Variant {
    rf_U32 tag;
    rf_address data;
} rf_Variant;

// Other types
typedef rf_S64 rf_Choice;
typedef void* rf_RoutineReference;

// Note: None is NOT a runtime type. In RazorForge, None represents the absence
// of a value and is encoded via discriminant fields (is_valid, state) in wrapper
// types like Maybe<T>, Result<T>, and Lookup<T>. There is no rf_None type.
typedef void* suflae_Object;
