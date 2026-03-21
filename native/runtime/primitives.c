// RazorForge primitive type operations
// These are simple wrappers that will eventually be inlined by codegen

#include <stdint.h>
#include <stdbool.h>

// S64 (signed 64-bit integer) operations
int64_t S64___add__(int64_t a, int64_t b) { return a + b; }
int64_t S64___sub__(int64_t a, int64_t b) { return a - b; }
int64_t S64___mul__(int64_t a, int64_t b) { return a * b; }
int64_t S64___floordiv__(int64_t a, int64_t b) { return a / b; }
int64_t S64___rem__(int64_t a, int64_t b) { return a % b; }
int64_t S64___neg__(int64_t a) { return -a; }
bool S64___eq__(int64_t a, int64_t b) { return a == b; }
bool S64___ne__(int64_t a, int64_t b) { return a != b; }
bool S64___lt__(int64_t a, int64_t b) { return a < b; }
bool S64___le__(int64_t a, int64_t b) { return a <= b; }
bool S64___gt__(int64_t a, int64_t b) { return a > b; }
bool S64___ge__(int64_t a, int64_t b) { return a >= b; }

// S32 (signed 32-bit integer) operations
int32_t S32___add__(int32_t a, int32_t b) { return a + b; }
int32_t S32___sub__(int32_t a, int32_t b) { return a - b; }
int32_t S32___mul__(int32_t a, int32_t b) { return a * b; }
int32_t S32___floordiv__(int32_t a, int32_t b) { return a / b; }
int32_t S32___rem__(int32_t a, int32_t b) { return a % b; }
int32_t S32___neg__(int32_t a) { return -a; }
bool S32___eq__(int32_t a, int32_t b) { return a == b; }
bool S32___ne__(int32_t a, int32_t b) { return a != b; }
bool S32___lt__(int32_t a, int32_t b) { return a < b; }
bool S32___le__(int32_t a, int32_t b) { return a <= b; }
bool S32___gt__(int32_t a, int32_t b) { return a > b; }
bool S32___ge__(int32_t a, int32_t b) { return a >= b; }

// F64 (64-bit float) operations
double F64___add__(double a, double b) { return a + b; }
double F64___sub__(double a, double b) { return a - b; }
double F64___mul__(double a, double b) { return a * b; }
double F64___div__(double a, double b) { return a / b; }
double F64___neg__(double a) { return -a; }
bool F64___eq__(double a, double b) { return a == b; }
bool F64___ne__(double a, double b) { return a != b; }
bool F64___lt__(double a, double b) { return a < b; }
bool F64___le__(double a, double b) { return a <= b; }
bool F64___gt__(double a, double b) { return a > b; }
bool F64___ge__(double a, double b) { return a >= b; }

// F32 (32-bit float) operations
float F32___add__(float a, float b) { return a + b; }
float F32___sub__(float a, float b) { return a - b; }
float F32___mul__(float a, float b) { return a * b; }
float F32___div__(float a, float b) { return a / b; }
float F32___neg__(float a) { return -a; }
bool F32___eq__(float a, float b) { return a == b; }
bool F32___ne__(float a, float b) { return a != b; }
bool F32___lt__(float a, float b) { return a < b; }
bool F32___le__(float a, float b) { return a <= b; }
bool F32___gt__(float a, float b) { return a > b; }
bool F32___ge__(float a, float b) { return a >= b; }

// Bool operations
bool Bool___and__(bool a, bool b) { return a && b; }
bool Bool___or__(bool a, bool b) { return a || b; }
bool Bool___not__(bool a) { return !a; }
bool Bool___eq__(bool a, bool b) { return a == b; }
bool Bool___ne__(bool a, bool b) { return a != b; }