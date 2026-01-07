// Main entry point wrapper for RazorForge executables
// Calls the RazorForge 'start' function

#include <stdio.h>
#include <stdint.h>
#include <stdbool.h>

// Forward declaration of RazorForge entry point
extern void start(void);

// Forward declarations for test functions
extern int64_t factorial(int64_t n);
extern int64_t add_s64(int64_t a, int64_t b);
extern int64_t max_s64(int64_t a, int64_t b);
extern int64_t min_s64(int64_t a, int64_t b);
extern int64_t abs_s64(int64_t n);
extern int64_t clamp(int64_t value, int64_t low, int64_t high);
extern int64_t fibonacci(int64_t n);

// Loop tests
extern int64_t sum_to_n(int64_t n);
extern int64_t factorial_iter(int64_t n);
extern int64_t count_digits(int64_t n);
extern int64_t power(int64_t base, int64_t exp);
extern int64_t gcd(int64_t a, int64_t b);
extern bool is_prime(int64_t n);

int main(int argc, char** argv) {
    // Call RazorForge entry point
    start();

    printf("=== RazorForge Codegen Test Suite ===\n\n");

    // Basic arithmetic
    printf("--- Basic Arithmetic ---\n");
    printf("  add_s64(10, 20) = %lld (expected: 30)\n", (long long)add_s64(10, 20));

    // Control flow
    printf("\n--- Control Flow ---\n");
    printf("  max_s64(15, 8) = %lld (expected: 15)\n", (long long)max_s64(15, 8));
    printf("  min_s64(15, 8) = %lld (expected: 8)\n", (long long)min_s64(15, 8));
    printf("  abs_s64(-42) = %lld (expected: 42)\n", (long long)abs_s64(-42));
    printf("  clamp(150, 0, 100) = %lld (expected: 100)\n", (long long)clamp(150, 0, 100));

    // Recursion
    printf("\n--- Recursion ---\n");
    printf("  factorial(5) = %lld (expected: 120)\n", (long long)factorial(5));
    printf("  fibonacci(10) = %lld (expected: 55)\n", (long long)fibonacci(10));

    // While loops
    printf("\n--- While Loops ---\n");
    printf("  sum_to_n(10) = %lld (expected: 55)\n", (long long)sum_to_n(10));
    printf("  factorial_iter(5) = %lld (expected: 120)\n", (long long)factorial_iter(5));
    printf("  count_digits(12345) = %lld (expected: 5)\n", (long long)count_digits(12345));

    // Nested control flow
    printf("\n--- Nested Control Flow ---\n");
    printf("  gcd(48, 18) = %lld (expected: 6)\n", (long long)gcd(48, 18));
    printf("  is_prime(17) = %s (expected: true)\n", is_prime(17) ? "true" : "false");
    printf("  is_prime(15) = %s (expected: false)\n", is_prime(15) ? "true" : "false");

    printf("\n=== All tests completed ===\n");
    return 0;
}