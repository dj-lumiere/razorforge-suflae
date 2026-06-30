/*
 * coro_abandon_spike.c — Phase 3/5b-2 cancellation shadow-stack harness (no RF front-end).
 *
 * Proves rf_coro_abandon walks a parked coroutine's shadow stack top-to-bottom and runs each
 * owned value's $destroy EXACTLY once, under v0.2.0 Mechanism C (per-VALUE nodes — the compiler
 * pushes one node per owned value at its construction, pops at its inline $destroy). Exercises:
 *   §7.6 double-fire invariant — a value inline-destroyed+popped before the park must NOT fire on
 *        a later abandon; a still-live value MUST. A built-in double-free detector guards this.
 *   §7.1 partial init       — a value not yet constructed at the park has no node, so it is never
 *        torn down (free, no fill-count needed).
 *
 * The push/pop here are HAND-WRITTEN; the compiler emits them in 5b-2. destroy_entity stands in
 * for a monomorphized `$destroy` called on a value's address (its `me`).
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

/* Stands in for a monomorphized `$destroy`: called on the value's ADDRESS (its `me`). The "value"
 * here is just an int holding the entity id, so we read it back and destroy that entity. */
static void destroy_entity(void* me)
{
    ent_destroy(*(int*)me);
}

/* ---- Bodies ------------------------------------------------------------------------------- */

typedef struct { int finished; } run_state;

/* Nested scopes: outer owns ent 0; inner owns ents 1,2 and PARKS after constructing only 1
 * (ent 2's node doesn't exist yet — partial init). Normal completion destroys 2,1 then 0. */
static void body_nested(void* ud)
{
    run_state* rs = (run_state*)ud;

    int e0 = 0; ent_construct(0);
    rf_cancel_frame n0; rf_coro_cf_push(&n0, &e0, destroy_entity);

    {
        int e1 = 1; ent_construct(1);
        rf_cancel_frame n1; rf_coro_cf_push(&n1, &e1, destroy_entity);

        rf_coro_yield(); /* PARK: ent 0 and 1 live, ent 2 NOT constructed (no node) */

        int e2 = 2; ent_construct(2);
        rf_cancel_frame n2; rf_coro_cf_push(&n2, &e2, destroy_entity);
        /* normal inner exit: reverse-order pop-then-destroy */
        rf_coro_cf_pop(&n2); ent_destroy(2);
        rf_coro_cf_pop(&n1); ent_destroy(1);
    }

    rf_coro_cf_pop(&n0); ent_destroy(0); /* normal outer exit */
    rs->finished = 1;
}

/* Sequential scopes: scope 1 (ent 0) FULLY destroys+pops before scope 2 (ent 1) parks.
 * Abandoning at the park must fire only ent 1's node — ent 0 is already gone, popped. */
static void body_sequential(void* ud)
{
    run_state* rs = (run_state*)ud;

    {
        int e0 = 0; ent_construct(0);
        rf_cancel_frame n0; rf_coro_cf_push(&n0, &e0, destroy_entity);
        rf_coro_cf_pop(&n0); ent_destroy(0); /* inline destroy + pop */
    }

    {
        int e1 = 1; ent_construct(1);
        rf_cancel_frame n1; rf_coro_cf_push(&n1, &e1, destroy_entity);
        rf_coro_yield(); /* PARK with only ent 1 live */
        rf_coro_cf_pop(&n1); ent_destroy(1);
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

    /* --- Scenario 1: normal completion — inline destroys run, abandon fires nothing --- */
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
        /* abandon a COMPLETED coroutine: nodes already popped, nothing more should fire */
        rf_coro_abandon(c);
        CHECK(destroy_count == 3, "abandon of completed coroutine fired a destroy");
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

        rf_coro_abandon(c); /* walk top-to-bottom: ent 1's node then ent 0's */

        CHECK(rs.finished == 0, "body must not have run its normal tail");
        CHECK(destroy_count == 2, "abandon should destroy exactly the 2 live entities");
        CHECK(destroy_log[0] == 1 && destroy_log[1] == 0,
              "abandon order should be reverse-construction: 1 then 0");
        CHECK(!double_free, "double-free during abandon");
        CHECK(all_dead(), "an entity leaked after abandon");
        /* ent 2 was never constructed and must never have been destroyed (partial init) */
        printf("scenario 2 (abandon @ partial init): OK\n");
    }

    /* --- Scenario 3: double-fire invariant — popped value must not fire on abandon --- */
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

        CHECK(destroy_count == 2, "abandon should fire only ent 1's node");
        CHECK(destroy_log[1] == 1, "abandon should destroy ent 1");
        CHECK(!double_free, "popped ent 0 fired its node -> double-free");
        CHECK(all_dead(), "an entity leaked");
        printf("scenario 3 (double-fire invariant): OK\n");
    }

    printf("OK: all abandon scenarios passed\n");
    return 0;
}
