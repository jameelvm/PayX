# PayX — a card payment system, built to learn its design

Teaching documentation for PayX, extended at the end of every phase. It
explains what was built, why, how to verify it, and the design talking points
each phase demonstrates. See `DESIGN.md` for the decision register and
`PROGRESS.md` for live build state.

## Phase 0 — Design

Nothing runnable yet. The design is in `DESIGN.md`; the one idea to carry
into every later phase:

> A payment system is not a throughput problem (≈1,400 TPS at peak is small).
> It is a **correctness-under-partial-failure** problem. Every phase answers
> some version of "the response was lost — did money move?"
