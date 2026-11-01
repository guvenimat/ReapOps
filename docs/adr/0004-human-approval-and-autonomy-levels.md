# ADR 0004: Human approval bound to the exact change

Status: accepted (2026-11-01)

## Context

The system proposes changes to production. Approvals must not be forgeable, replayable or applicable to something other than what the person saw.

## Decision

Every action outside the autoscaler's range needs an approval. Notifications link to an approval page; approving is an authenticated POST. An approval binds to the proposal hash, commit SHA and image digest, expires, and is audited. The approval matrix is configurable per risk level; the default is a single approval, and two approvals can be required for high-risk actions. No answer means no action.

## Consequences

- Email link scanners and stale approvals cannot trigger changes.
- Some incidents resolve by themselves before anyone approves; their proposals expire.
