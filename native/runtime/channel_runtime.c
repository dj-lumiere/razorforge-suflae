/*
 * channel_runtime.c — native backend for RazorForge channels (`Hopper[T]` / `Conveyor[T]`).
 *
 * A channel is a refcounted ring buffer carrying a stream of `T`-payload pointers between concurrent
 * agents. It builds directly on the v0.2.0 concurrency substrate: a parked CONSUMER (empty buffer)
 * or PRODUCER (full buffer) is, inside a scheduler-driven coroutine, parked via rf_sched_park_external
 * and woken by rf_sched_wake from the counterpart op on ANY thread; on a plain OS thread it blocks on
 * the channel's condition variable. This is the same uncolored park-or-block contract as
 * retrieve!/waitfor (see razorforge_runtime.h, task_runtime.c).
 *
 * Capacity is the number of BUFFERED slots (items the channel holds with no producer blocked and no
 * consumer present):
 *   - capacity N >= 1 : bounded ring buffer; `feed` blocks/parks when full = backpressure.
 *   - capacity 0       : rendezvous — `feed` deposits one item and waits until a consumer takes it,
 *                        so a completed `feed` means the hand-off happened (synchronization point).
 *
 * Lifetime: producer (Feeder) and consumer (Hopper/LineWorker) handles are refcounted separately.
 * The last Feeder dropping auto-closes the channel (consumers then drain, then see "closed"). The
 * struct is freed when BOTH refcounts reach 0. Undelivered payloads still in the buffer at teardown
 * are reported to the caller's destroy hook so owned `T` is not leaked.
 */
#include "types.h"
#include "../include/razorforge_runtime.h"
#include "rf_sync.h"

#include <stdlib.h>
#include <string.h>

/* Raised on an allocation failure (stacktrace.c) instead of returning NULL downstream. */
extern void __rf_throw(const char* error_type, const char* message);

/* `typedef struct rf_channel rf_channel;` + the rf_channel_* prototypes live in razorforge_runtime.h;
 * the struct layout below is private to this translation unit. */

/* A parked COROUTINE waiter: (scheduler, coroutine) so a counterpart op on any thread can wake it via
 * rf_sched_wake. Thread waiters need no node — they block on the channel cond and re-check. Nodes live
 * on the parked party's own (coroutine) stack while linked, and are unlinked before it returns. */
typedef struct rf_chan_waiter
{
    rf_sched* sched;
    rf_coro* coro;
    struct rf_chan_waiter* next;
} rf_chan_waiter;

struct rf_channel
{
    rf_mutex lock;
    rf_cond cond; /* thread waiters (producers-when-full + consumers-when-empty) block here */

    void** slots;       /* ring buffer; max(capacity,1) entries of payload pointers */
    uint64_t capacity;  /* buffered slots; 0 = rendezvous */
    uint64_t slot_cap;  /* physical slot count = max(capacity, 1) */
    uint64_t head;      /* index of the next item to read */
    uint64_t count;     /* items currently buffered */

    uint32_t producer_refs; /* live Feeder handles */
    uint32_t consumer_refs; /* live Hopper / LineWorker handles */
    int closed;             /* 1 once closed (explicit close or last Feeder dropped) */

    rf_chan_waiter* not_full_head;  /* producers parked waiting for a free slot */
    rf_chan_waiter* not_empty_head; /* consumers parked waiting for an item */
};

/* ---- coroutine-waiter list helpers (caller holds chan->lock) ---------------------------------- */

static void chan_waiter_push(rf_chan_waiter** head, rf_chan_waiter* node)
{
    node->next = *head;
    *head = node;
}

static void chan_waiter_remove(rf_chan_waiter** head, rf_chan_waiter* node)
{
    while (*head != NULL) {
        if (*head == node) {
            *head = node->next;
            node->next = NULL;
            return;
        }
        head = &(*head)->next;
    }
}

/* Wake every coroutine parked on `*head` (idempotent per park via rf_sched_wake's in_ready dedup) AND
 * every blocked thread (cond broadcast). Each woken party re-checks its predicate and removes its own
 * node. Caller holds chan->lock. */
static void chan_wake_all(rf_channel* chan, rf_chan_waiter* head)
{
    for (rf_chan_waiter* w = head; w != NULL; w = w->next) {
        rf_sched_wake(w->sched, w->coro);
    }
    rf_cond_broadcast(&chan->cond);
}

/* ---- create / refcount / free ----------------------------------------------------------------- */

rf_channel* rf_channel_create(uint64_t capacity)
{
    uint64_t slot_cap = capacity == 0 ? 1 : capacity;

    /* Guard capacity * sizeof(void*) against overflow before allocating (decision §0.2): a wild
     * capacity must surface as a diagnosed OutOfMemoryError, not a wrapped tiny allocation. */
    if (slot_cap > (uint64_t)SIZE_MAX / sizeof(void*)) {
        __rf_throw("OutOfMemoryError", "channel capacity too large to allocate");
        return NULL;
    }

    rf_channel* chan = (rf_channel*)calloc(1, sizeof(rf_channel));
    if (chan == NULL) {
        __rf_throw("OutOfMemoryError", "failed to allocate channel");
        return NULL;
    }
    chan->slots = (void**)calloc((size_t)slot_cap, sizeof(void*));
    if (chan->slots == NULL) {
        free(chan);
        __rf_throw("OutOfMemoryError", "failed to allocate channel buffer");
        return NULL;
    }

    rf_mutex_init(&chan->lock);
    rf_cond_init(&chan->cond);
    chan->capacity = capacity;
    chan->slot_cap = slot_cap;
    chan->producer_refs = 1; /* the Feeder returned by make_* */
    chan->consumer_refs = 1; /* the Hopper / LineWorker returned by make_* */
    return chan;
}

/* Free the struct iff both refcounts are 0. Caller must NOT hold the lock (we destroy it). Returns
 * 1 if freed. */
static int chan_maybe_free(rf_channel* chan)
{
    if (chan->producer_refs != 0 || chan->consumer_refs != 0) {
        return 0;
    }
    rf_mutex_destroy(&chan->lock);
    rf_cond_destroy(&chan->cond);
    free(chan->slots);
    free(chan);
    return 1;
}

void rf_channel_add_feeder(rf_channel* chan)
{
    if (chan == NULL) return;
    rf_mutex_lock(&chan->lock);
    chan->producer_refs++;
    rf_mutex_unlock(&chan->lock);
}

void rf_channel_add_consumer(rf_channel* chan)
{
    if (chan == NULL) return;
    rf_mutex_lock(&chan->lock);
    chan->consumer_refs++;
    rf_mutex_unlock(&chan->lock);
}

/* Drop a Feeder. The LAST feeder dropping auto-closes the channel (Rust-mpsc style): consumers drain
 * the remaining buffer, then see closed and finish. */
void rf_channel_drop_feeder(rf_channel* chan)
{
    if (chan == NULL) return;
    rf_mutex_lock(&chan->lock);
    if (chan->producer_refs > 0) {
        chan->producer_refs--;
    }
    if (chan->producer_refs == 0 && !chan->closed) {
        chan->closed = 1;
        chan_wake_all(chan, chan->not_empty_head); /* let consumers observe close + drain */
    }
    rf_mutex_unlock(&chan->lock);
    chan_maybe_free(chan);
}

/* Drop a consumer. With no consumers left, wake blocked producers so a full-buffer `feed` does not
 * hang forever (it will observe consumer_refs == 0 and fail). */
void rf_channel_drop_consumer(rf_channel* chan)
{
    if (chan == NULL) return;
    rf_mutex_lock(&chan->lock);
    if (chan->consumer_refs > 0) {
        chan->consumer_refs--;
    }
    if (chan->consumer_refs == 0) {
        chan_wake_all(chan, chan->not_full_head);
    }
    rf_mutex_unlock(&chan->lock);
    chan_maybe_free(chan);
}

void rf_channel_close(rf_channel* chan)
{
    if (chan == NULL) return;
    rf_mutex_lock(&chan->lock);
    if (!chan->closed) {
        chan->closed = 1;
        /* Wake everyone: consumers drain-then-finish; producers blocked on a full buffer fail. */
        chan_wake_all(chan, chan->not_empty_head);
        chan_wake_all(chan, chan->not_full_head);
    }
    rf_mutex_unlock(&chan->lock);
}

/* ---- the load-bearing wait: park a coroutine or block a thread until `predicate` flips ---------- */

/* Returns once the calling party should re-evaluate. Coroutine path: register a node on `list`, drop
 * the lock, rf_sched_park_external, re-take the lock, unlink. Thread path: rf_cond_wait under the
 * lock. Caller holds chan->lock on entry and exit, and re-checks the predicate in a loop. */
static void chan_wait(rf_channel* chan, rf_chan_waiter** list)
{
    rf_sched* sched = rf_sched_current();
    rf_coro* self = rf_coro_current();
    if (sched != NULL && self != NULL) {
        rf_chan_waiter node = { sched, self, NULL };
        chan_waiter_push(list, &node);
        rf_mutex_unlock(&chan->lock);
        rf_sched_park_external(); /* woken by chan_wake_all from a counterpart op on any thread */
        rf_mutex_lock(&chan->lock);
        chan_waiter_remove(list, &node);
    } else {
        rf_cond_wait_forever(&chan->cond, &chan->lock);
    }
}

/* ---- feed / next ------------------------------------------------------------------------------ */

/* Send one payload. Returns 1 on success, 0 if the channel is closed or has no consumers (the RF
 * surface lowers 0 to a failable throw). Blocks/parks while the buffer is full (backpressure); for a
 * rendezvous channel (capacity 0) it additionally waits until a consumer has taken the item. */
rf_U32 rf_channel_feed(rf_channel* chan, void* payload)
{
    if (chan == NULL) return 0;
    rf_mutex_lock(&chan->lock);

    /* Wait for a free slot. consumer_refs == 0 means nobody can ever drain — fail instead of hang.
     * Cooperative cancellation: if the calling agent has been asked to stop, abandon the send rather
     * than park (mirrors the interruptible waitfor). */
    while (chan->count == chan->slot_cap && !chan->closed && chan->consumer_refs > 0) {
        if (rf_cancel_requested()) {
            rf_mutex_unlock(&chan->lock);
            return 0;
        }
        chan_wait(chan, &chan->not_full_head);
    }
    if (chan->closed || chan->consumer_refs == 0) {
        rf_mutex_unlock(&chan->lock);
        return 0;
    }

    uint64_t tail = (chan->head + chan->count) % chan->slot_cap;
    chan->slots[tail] = payload;
    chan->count++;
    chan_wake_all(chan, chan->not_empty_head); /* a consumer can now proceed */

    if (chan->capacity == 0) {
        /* Rendezvous: do not return until THIS item has been taken (count drops back to 0), so a
         * completed feed means the hand-off happened. Honor cancellation while waiting for the taker. */
        while (chan->count > 0 && !chan->closed && chan->consumer_refs > 0) {
            if (rf_cancel_requested()) {
                rf_mutex_unlock(&chan->lock);
                return 1; /* the item was deposited; a consumer may still take it */
            }
            chan_wait(chan, &chan->not_full_head);
        }
    }
    rf_mutex_unlock(&chan->lock);
    return 1;
}

/* Receive one payload. Blocks/parks while the buffer is empty. Returns the payload, or NULL when the
 * channel is closed AND drained (the RF surface maps NULL to the Iterator's absent → loop ends). */
void* rf_channel_next(rf_channel* chan)
{
    if (chan == NULL) return NULL;
    rf_mutex_lock(&chan->lock);

    /* Cooperative cancellation: a parked receive ends the stream (returns NULL -> Iterator absent ->
     * `for` loop ends) when the calling agent has been asked to stop, mirroring waitfor. */
    while (chan->count == 0 && !chan->closed) {
        if (rf_cancel_requested()) {
            rf_mutex_unlock(&chan->lock);
            return NULL;
        }
        chan_wait(chan, &chan->not_empty_head);
    }
    if (chan->count == 0) { /* closed and drained */
        rf_mutex_unlock(&chan->lock);
        return NULL;
    }

    void* payload = chan->slots[chan->head];
    chan->slots[chan->head] = NULL;
    chan->head = (chan->head + 1) % chan->slot_cap;
    chan->count--;
    chan_wake_all(chan, chan->not_full_head); /* a producer (or a rendezvous feed) can now proceed */

    rf_mutex_unlock(&chan->lock);
    return payload;
}

/* Snapshot of buffered item count (test/diagnostic helper; not a synchronization primitive). */
uint64_t rf_channel_count(rf_channel* chan)
{
    if (chan == NULL) return 0;
    rf_mutex_lock(&chan->lock);
    uint64_t n = chan->count;
    rf_mutex_unlock(&chan->lock);
    return n;
}
