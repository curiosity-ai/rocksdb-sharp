# Planned switchover: replica → primary, for rolling upgrades

This folder adds planned switchover to the primary/replica WAL replication in
`csharp/src/Replication`. The primary hands its role to an up-to-date replica
without losing an acknowledged write. That makes a zero-data-loss rolling
upgrade of a RocksDB-backed service possible:

```
A primary, B replica    →  hand off A → B  →  stop A, upgrade, start A  (A rejoins as replica from the WAL)
                        →  hand off B → A  →  stop B, upgrade, start B  (B rejoins as replica from the WAL)
```

The library half is transport-agnostic, like `csharp/src/Cluster`. The
MagicOnion wiring and a coordinator that runs the scenarios below under live
client writes are in `Tests/SwitchoverTest`.

| File | Purpose |
|------|---------|
| `ReplicationRoleState.cs` | Crash-safe `(epoch, promisedEpoch, role, primaryId, handoffTarget)` plus the epoch map (`startSeq → epoch`), written via atomic temp-file rename. |
| `PrimaryWriteGate.cs` | Write admission with three states: **open**, **holding** (new writes wait), **closed** (writes are refused and redirected). Holding also waits for in-flight writes to drain, so the sequence number read afterwards is final. |
| `SwitchoverNode.cs` | The state machine: `HandOffAsync` (primary side), `HandlePromoteAsync` / `HandleRejectEpochAsync` (replica side), `ResolveAsync` (settles an interrupted handoff), `CheckReplicaLog` (attach check), `AcceptStream` (stream fencing), `ForcePromoteAsync` (unplanned failover). |

---

## What a planned switchover has to guarantee

| Requirement | How it is met |
|---|---|
| **No acknowledged write is lost.** Replication is asynchronous, so the replica is always a little behind. | The primary *holds* writes (new ones wait, in-flight ones finish). Its latest sequence number then becomes the **handoff point**. The replica takes over only once it holds exactly that point. |
| **Never two writable primaries**, including when RPCs time out, answers are lost, or requests arrive late. | **Epochs**, the equivalent of Raft terms. A replica *promises* (fsyncs) the proposed epoch before acting on it. A primary that gets no answer makes the replica promise the epoch explicitly (`RejectEpoch`), so a delayed promote can never succeed. Only then does the primary resume, in a newer epoch. If the replica cannot be reached, the primary keeps refusing writes. Unavailable is better than split. |
| **A node never boots as primary just because its config says so.** | Role state is persisted. Before a handoff starts, the primary persists `HandingOff`, so a crash in the middle restarts it read-only until it has asked the other node how the handoff ended. |
| **The old primary rejoins cheaply.** | A replica applies the primary's entries at exactly the same sequence numbers. After the handoff, the old primary's log is a prefix of the new primary's, so it streams from `handoffSeq + 1` with no snapshot. The attach check (`latestSeq`, epoch at `latestSeq`) confirms the prefix property. |
| **Divergence is detected, not merged.** | After an *unplanned* failover, the old primary may hold writes that were never replicated. The attach check sees that its tail carries another epoch, and the node is rebuilt from a snapshot. A RocksDB WAL cannot be truncated. |
| **Clients find the new primary.** | A write held during the switch completes on the old primary if the handoff aborts. If it completes, the write is refused with a `PrimaryHint` and the client retries on the new primary. In production this is service discovery, a proxy, or DNS. |
| **A bad switch is cheap to back out of.** | A lag check happens before anything is held (`Rejected`, zero impact). A catch-up timeout bounds the time writes are held. The primary resumes on its own if the replica does not reach the handoff point (`Aborted`). |

## The protocol

```
 primary A (epoch E)                                    replica B
 ───────────────────                                    ─────────
 GetStatus ───────────────────────────────────────────► role=Replica, primary=A, lag ≤ MaxLagToStart?
                                                         (no → Rejected, nothing changed)
 promise P = max(promised A, promised B) + 1
 persist HandingOff(E, target=B)
 gate.Hold()   ← new writes wait, in-flight drain
 F = latestSeq                    ── WAL stream keeps flowing ──►
 Promote(P, from=A, F, timeout) ──────────────────────► persist promise P
                                                         wait until latestSeq == F (≤ timeout)
                                                         stop ingesting
                                                         epochs: F+1 → P; persist Primary(P)
                                                         gate.Open()   ← B takes writes from F+1
 ◄──────────────────────────────────────────────────── Promoted(P)
 persist Replica(P, primary=B); gate.Close()
   held writes → NotPrimary(hint=B)
 attach to B: CheckReplicaLog(F, epoch(F)) ✓  stream from F+1

 no answer / refused:
 RejectEpoch(P) ──────────────────────────────────────► cancel the catch-up; persist promise P
 ◄──────────────────────────────────────────────────── status
   B Primary(P)        → finish as above (the answer was lost)
   B promised ≥ P      → resume as Primary(promised + 1): Aborted
   unreachable         → stay closed: Unresolved, ResolveAsync retries
```

The epoch map plays the role of the Raft term log in `csharp/src/Cluster`, and
reuses its `RaftLogTerms`. Every node keeps `startSeq → epoch` ranges. A
replica merges the primary's ranges when it attaches, and again whenever the
stream's epoch tag changes. When a node becomes primary, it drops ranges past
its own tail before starting its epoch, because a replica may have learned
them for entries it never received.

## Verified scenarios (`Tests/SwitchoverTest`)

Each scenario runs two node processes, four client writers going as fast as
they can, and full validation at the end:

- every acknowledged key is on both nodes;
- whole-database checksums are identical;
- no epoch ever had two primaries, and no node's epoch ever went backwards.

| Scenario | What happens | Result |
|---|---|---|
| `upgrade` | A→B handoff, kill and restart A, B→A handoff, kill and restart B. | Both restarted nodes rejoin from the WAL with **no snapshot**. No acknowledged write lost (~70k writes). |
| `abort` | Replica ingest paused. | Lag check → `Rejected` (writes never held). With the check bypassed → `Aborted` after the 1s catch-up timeout, and A resumes in a new epoch that B then follows. A later handoff → `Completed`. |
| `lost-reply` | B promotes, but its answer is dropped. | A asks via `RejectEpoch`, learns B is primary, and completes. |
| `late-promote` | The promote request is delayed 6s, past A's deadline. | A makes B promise the epoch and resumes. When the stale request lands, B refuses it. Still one primary. |
| `unreachable` | B is SIGSTOPped mid-handoff. | A reports `Unresolved` and **refuses writes** instead of guessing. After SIGCONT, the handoff settles with exactly one primary. |
| `force` | Primary killed while the replica lags. Operator force-promotes B. A restarts. | A sees B is primary in a newer epoch and steps down. The attach check finds its tail diverged, and A rebuilds from a snapshot. The writes A acknowledged but never replicated are lost, as expected for async replication. |

```
dotnet build Tests/SwitchoverTest/SwitchoverTest.csproj -c Release
dotnet Tests/SwitchoverTest/bin/Release/net10.0/SwitchoverTest.dll all   # or one scenario by name
```

### Measured cost (localhost, this prototype)

- **Writes held per handoff: ~10–50 ms.** The replica is writable 4–10 ms
  after the hold starts. The rest is the gRPC round trip back to the old
  primary, which is when the held writes get redirected.
- **Longest gap without an acknowledged write, seen by clients:
  ~65–220 ms** per rolling upgrade. That includes the redirect and retry.
- With a lost answer, add one RPC round trip (~80 ms held). An aborted
  handoff holds writes for the catch-up timeout.
- A finding while measuring: the replica's detach first took 30–50 ms. That
  time went into tearing down the gRPC stream, because
  `CancellationTokenSource.Cancel()` runs the stream's cancellation callbacks
  synchronously. The host now stops *ingesting* under a lock and cancels with
  `CancelAsync()`, which brought detach under 1 ms.

---

## Findings that matter for upgrades

1. **WAL retention decides whether a restarted node rejoins in seconds or
   needs a full copy.** A node that was down for an upgrade needs the other
   node's WAL to reach back to where it stopped. That depends on
   `WAL_ttl_seconds` / `WAL_size_limit_MB` (the archive). The replication
   sample uses a 10 s TTL, so any upgrade longer than 10 s falls back to a
   snapshot. The prototype uses 600 s. Size it to *longest expected downtime
   × write rate*, on both nodes, since either can be primary.

2. **The snapshot fallback is effectively a full copy after a switchover.**
   Each node flushes its own SSTs, so file names and contents diverge across
   the pair. The per-file delta transfer (`ReplicationDelta`) reuses almost
   nothing. Plan the bandwidth and time for it, or make it rare with
   retention (point 1).

3. **Mixed RocksDB versions during the rollout.** Replication ships raw
   write-batch bytes (`WalTailer` concatenates batch bodies), and a snapshot
   ships SST/MANIFEST files.
   - Newer RocksDB reads older formats. Older RocksDB may not read newer
     ones: new record types, `format_version`, blob/WAL features.
   - So, upgrade the **replica first**, switch over to it, then stop the old
     primary and upgrade it **before** it rejoins. Do not leave an old-version
     node following a new-version primary.
   - Keep `format_version` and other format-changing options pinned until
     both nodes run the new version, so a rollback can still open the files.
   - The same applies to the application's own wire format (the MessagePack
     DTOs) and data schema: both versions run side by side during the
     window.

4. **"Same sequence numbers on both nodes" is the invariant everything rests
   on.** Anything that breaks it makes the attach check or the per-chunk
   check force a resync, or silently loses data:
   - writes on a replica;
   - writes with `disableWAL` on the primary, which are never replicated;
   - `IngestExternalFile` on the primary, which bypasses the WAL;
   - starting the tailer mid-record: `GetUpdatesSince` starts at the batch
     *containing* the requested number, and `WalTailer` then includes the
     already-applied entries of that batch.

   The prototype checks `chunk.FirstSeq == localLatest + 1` and resyncs on a
   mismatch. `ClusterNodeHost` has the same check. The plain replication
   sample's replica does not.

5. **Two nodes can switch over safely, but cannot fail over automatically.**
   A planned switchover is safe because both nodes take part. With the
   primary *gone*, two nodes cannot tell "dead" from "partitioned".
   `ForcePromoteAsync` exists for an operator's decision, and it loses the
   writes the dead primary had not replicated. Automatic failover needs one
   of these:
   - a third vote: the Raft cluster in `csharp/src/Cluster`, or a witness;
   - a fence that guarantees the old primary is down, such as STONITH or a
     lease in an external store;
   - synchronous replication, so nothing acknowledged is ever only on one
     node.

6. **Client writes that time out are ambiguous.** A write that timed out may
   or may not have been applied. The test client retries the same key, which
   is idempotent. Non-idempotent operations need request ids, or a dedup
   table written in the same batch.

## The Raft cluster alternative

`csharp/src/Cluster` already fails over automatically. For a rolling
upgrade, restarting followers one at a time is free. Restarting the leader
today costs an election: about 1.5–3 s with no writes, the
`ElectionTimeout` range. The standard fix is **leadership transfer**
(§3.10 of the Raft dissertation):

1. The leader stops accepting writes.
2. It waits until the target's `matchSeq` equals its latest seq.
3. It sends `TimeoutNow`.
4. The target starts an election at once, with no timeout.

That is the same hold → catch up → hand over shape as this switchover, with
the vote taking the place of the promise. In `RaftClusterNode` it is roughly
one new RPC, a "transferring" flag that rejects writes, and a
start-election-now path, on the order of 100–150 lines plus a coordinator
scenario. Use the cluster if the deployment can run three nodes and
automatic failover is wanted. Use this pair switchover if it must stay at
two nodes.

## What a production version still needs

- **Routing.** Service discovery or a proxy, instead of the test client's
  hint-following. A readiness signal ("I am primary") for the load balancer.
- **Deploy integration.** A `handoff-then-stop` hook (e.g. a Kubernetes
  `preStop`) that calls `HandOffAsync` and refuses to stop the process on
  `Rejected`/`Unresolved`. A health check that waits for "replica, caught up"
  before the next node is touched.
- **Security.** Authentication and authorization on the switchover and
  replication RPCs.
- **WAL retention by progress.** The `ReplicationService` sample retires
  archived WALs by replica progress. Combine that with a TTL floor covering
  the upgrade window. A replica that is down must not let the primary delete
  what it will need.
- **Observability.** Replica lag, time held per handoff, outcome counters,
  and alerts on `Unresolved`.
- **Choosing a stack.** Merge the two harnesses' service code, the plain
  replication sample and this one, into one host. Or move the switchover
  onto the Raft cluster as leadership transfer (above).
