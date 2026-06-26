/*
 * coro_abandon_spike.c — Phase 3 cancellation shadow-stack harness (no RF front-end).
 *
 * Proves rf_coro_abandon walks a parked coroutine's shadow stack innermost-first and runs each
 * scope's teardown thunk EXACTLY once, exercising the two sharp edges from the design doc:
 *   §7.6 double-fire invariant — a scope that exited normally (popped) must NOT fire on a later
 *        abandon; a still-live scope MUST. A built-in double-free detector guards this.
 *   §7.1 partial init       — a scope parked mid-construction tears down only the constructed
 *        prefix of its locals, never the not-yet-built tail.
 *
 * The thunks here are HAND-WRITTEN (the compiler synthesizes them in Phase 5). They model RF's
 * reverse-declaration-order $destroy chain over a scope's owned entities.
 *
 * Build (Windows x64, clang):
 *   clang -std=c23 -I native/include -I native/libco -DHAVE_LIBCO \
 *       native/tests/coro_abandon_spike.c native/runtime/coro_runtime.c \
 *       native/runtime/concurrency_context.c native/libco/libco.c -o build/coro_abandon_spike.exe
 *
 * Exit code 0 = all assertions passed.
 */

#include "razorforge_runtime.h"

#include <stdio.h>

/* ---- Entity registry: a toy "owned heap value" with a double-free detector ---------------- */

#define MAX_ENT 8

static int ent_alive[MAX_ENT];     /* 1 between construct and destroy */
static int destroy_log[MAX_ENT];   /* ids in the order they were destroyed */
static int destroy_count;
static int double_free;            /* set if any entity is destroyed twice */

static void registry_reset(void)
{
    for (int i = 0; i < MAX_ENT; i++) {
        ent_alive[i] = 0;
        destroy_log[i] = -1;
    }
    destroy_count = 0;
    double_free = 0;
}

static void ent_construct(int id) { ent_alive[id] = 1; }

static void ent_destroy(int id)
{
    if (!ent_alive[id]) {
        double_free = 1; /* double-free or destroy-before-construct */
        return;
    }
    ent_alive[id] = 0;
    destroy_log[destroy_count++] = id;
}

/* ---- A scope's locals + its hand-written teardown thunk ----------------------------------- */

/* Owned-entity ids in declaration order; `inited` = how many are currently constructed (the
 * partial-init prefix). The thunk destroys the live prefix in REVERSE declaration order. */
typedef struct {
    int ids[4];
    int inited;
} scope_locals;

static void scope_teardown(void* base)
{
    scope_locals* s = (scope_locals*)base;
    for (int i = s->inited - 1; i >= 0; i--) {
        ent_destroy(s->ids[i]);
    }
}

/* ---- Bodies ------------------------------------------------------------------------------- */

typedef struct { int finished; } run_state;

/* Nested scopes: outer owns ent 0; inner owns ents 1,2 and PARKS after constructing only 1
 * (ent 2 not yet built — partial init). Normal completion destroys 2,1 then 0 inline. */
static void body_nested(void* ud)
{
    run_state* rs = (run_state*)ud;

    scope_locals outer = { .ids = { 0 }, .inited = 0 };
    rf_cancel_frame cf_outer;
    rf_coro_cf_push(&cf_outer, scope_teardown, &outer);
    ent_construct(0); outer.inited = 1;

    {
        scope_locals inner = { .ids = { 1, 2 }, .inited = 0 };
        rf_cancel_frame cf_inner;
        rf_coro_cf_push(&cf_inner, scope_teardown, &inner);
        ent_construct(1); inner.inited = 1;

        rf_coro_yield(); /* PARK: ent 0 and 1 live, ent 2 NOT constructed */

        ent_construct(2); inner.inited = 2;
        /* normal inner exit: inline reverse-order destroy, then pop without firing */
        ent_destroy(2); ent_destroy(1); inner.inited = 0;
        rf_coro_cf_pop(&cf_inner);
    }

    ent_destroy(0); outer.inited = 0; /* normal outer exit */
    rf_coro_cf_pop(&cf_outer);
    rs->finished = 1;
}

/* Sequential scopes: scope 1 (ent 0) FULLY completes and pops before scope 2 (ent 1) parks.
 * Abandoning at the park must fire only scope 2's thunk — ent 0 is already gone, popped. */
static void body_sequential(void* ud)
{
    run_state* rs = (run_state*)ud;

    {
        scope_locals s1 = { .ids = { 0 }, .inited = 0 };
        rf_cancel_frame cf1;
        rf_coro_cf_push(&cf1, scope_teardown, &s1);
        ent_construct(0); s1.inited = 1;
        ent_destroy(0); s1.inited = 0; /* inline destroy */
        rf_coro_cf_pop(&cf1);          /* popped: thunk must never fire for ent 0 */
    }

    {
        scope_locals s2 = { .ids = { 1 }, .inited = 0 };
        rf_cancel_frame cf2;
        rf_coro_cf_push(&cf2, scope_teardown, &s2);
        ent_construct(1); s2.inited = 1;
        rf_coro_yield(); /* PARK with only ent 1 live */
        ent_destroy(1); s2.inited = 0;
        rf_coro_cf_pop(&cf2);
    }
    rs->finished = 1;
}

#define CHECK(cond, msg)                                                       \
    do {                                                                       \
        if (!(cond)) {                                                         \
            fprintf(stderr, "FAIL: %s  (%s:%d)\n", (msg), __FILE__, __LINE__); \
            return 1;                                                          \
        }                                                                      \
    } while (0)

static int all_dead(void)
{
    for (int i = 0; i < MAX_ENT; i++) {
        if (ent_alive[i]) return 0;
    }
    return 1;
}

int main(void)
{
    printf("coro backend: %s\n", rf_context_backend_name());

    /* --- Scenario 1: normal completion — inline destroys run, thunks NEVER fire --- */
    {
        registry_reset();
        run_state rs = { 0 };
        rf_coro* c = rf_coro_create(body_nested, &rs, 0);
        CHECK(c != NULL, "create failed");
        while (rf_coro_resume(c) != RF_CORO_COMPLETED) { }
        CHECK(rs.finished == 1, "body did not finish");
        /* inline reverse-order teardown: inner (2,1) then outer (0) */
        CHECK(destroy_count == 3, "expected 3 inline destroys");
        CHECK(destroy_log[0] == 2 && destroy_log[1] == 1 && destroy_log[2] == 0,
              "inline destroy order should be 2,1,0");
        CHECK(!double_free, "double-free on normal completion");
        /* abandon a COMPLETED coroutine: frames already popped, nothing more should fire */
        rf_coro_abandon(c);
        CHECK(destroy_count == 3, "abandon of completed coroutine fired a thunk");
        CHECK(all_dead(), "an entity leaked after normal completion");
        printf("scenario 1 (normal completion): OK\n");
    }

    /* --- Scenario 2: abandon while parked at PARTIAL INIT --- */
    {
        registry_reset();
        run_state rs = { 0 };
        rf_coro* c = rf_coro_create(body_nested, &rs, 0);
        CHECK(c != NULL, "create failed");
        CHECK(rf_coro_resume(c) == RF_CORO_PARKED, "first resume should park");
        /* parked with ent 0,1 live, ent 2 never constructed */
        CHECK(ent_alive[0] && ent_alive[1] && !ent_alive[2], "unexpected live set at park");

        rf_coro_abandon(c); /* walk innermost-first: inner thunk (ent 1) then outer (ent 0) */

        CHECK(rs.finished == 0, "body must not have run its normal tail");
        CHECK(destroy_count == 2, "abandon should destroy exactly the 2 live entities");
        CHECK(destroy_log[0] == 1 && destroy_log[1] == 0,
              "abandon order should be innermost-first: 1 then 0");
        CHECK(!double_free, "double-free during abandon");
        CHECK(all_dead(), "an entity leaked after abandon");
        /* ent 2 was never constructed and must never have been destroyed (partial init) */
        printf("scenario 2 (abandon @ partial init): OK\n");
    }

    /* --- Scenario 3: double-fire invariant — popped scope must not fire on abandon --- */
    {
        registry_reset();
        run_state rs = { 0 };
        rf_coro* c = rf_coro_create(body_sequential, &rs, 0);
        CHECK(c != NULL, "create failed");
        CHECK(rf_coro_resume(c) == RF_CORO_PARKED, "first resume should park");
        /* ent 0 already inline-destroyed+popped; only ent 1 live */
        CHECK(!ent_alive[0] && ent_alive[1], "scope 1 should be gone, scope 2 live");
        CHECK(destroy_count == 1 && destroy_log[0] == 0, "ent 0 should be destroyed once inline");

        rf_coro_abandon(c);

        CHECK(destroy_count == 2, "abandon should fire only scope 2's thunk");
        CHECK(destroy_log[1] == 1, "abandon should destroy ent 1");
        CHECK(!double_free, "popped scope 1 fired its thunk -> double-free");
        CHECK(all_dead(), "an entity leaked");
        printf("scenario 3 (double-fire invariant): OK\n");
    }

    printf("OK: all abandon scenarios passed\n");
    return 0;
}