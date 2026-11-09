# Changelog

## v0.4.1

- Delivery logging: each time a drone completes a delivery into a demand
  inventory, one concise line is logged at Info level —
  `[t=..] DELIVERED group=<id> toDemandInv=<invId> fill=<count>/<size> dist=<supply->demand meters>`.
  This makes a demand inventory's fill curve (how fast it reaches capacity)
  directly observable as a timestamped series. Log-only; no change to dispatch
  behavior. Toggle with the new `Logging.DeliveryLog` config (default on).

## v0.4.0

- `PriorityThenAge` task scoring: tasks are scored by request priority combined
  with an age term, so urgent work is served first while anti-starvation aging
  guarantees long-waiting tasks still get served.

## v0.3.0

- Dual-role containers: a container can be both a supply and a demand for the
  same item, enabling buffer/relay chains, with an anti-ping-pong guard to stop
  items bouncing between two containers.

## v0.2.1

- Log readability: timestamps and resolved item names in the assignment log.

## v0.2.0

- Full dispatch replacement: nearest-supply binding, cross-planet stall fix,
  nearest-task assignment, and an auto-fallback to the stock coroutine on error.

## v0.1.0

- Starvation fix: oldest-first pickup with per-task age tracking.
