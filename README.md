# Azure Batch Autoscale — Query Changes

This document describes the changes made to the Azure Batch autoscale pool query code, which is part of the orchestrator responsible for managing render job distribution across Azure Batch pools.

---

## Files

| File | Description |
|------|-------------|
| `CloudPool pool Query.cs` | Original code with core bug fixes and scaling logic improvements |
| `CloudPool pool Query Refined.cs` | Extends the above with timezone-aware preload logic for peak hour demand |

---

## `CloudPool pool Query.cs` — Core fixes

### 1. Typo fix: `_scaleForumula` → `_scaleFormula`
The field name was misspelled, which would cause a compile error if the correct field name is used elsewhere in the codebase.

### 2. Cooldown check: `TimeSpan.FromSeconds(305)` → `TimeSpan.FromMinutes(5)`
The original 305-second value represented the same 5-minute intent but was non-obvious. Replaced with an explicit `FromMinutes(5)` to match the documented 5-minute locking window and improve readability.

### 3. Allocation state guard: `not AllocationState.Resizing` → `AllocationState.Steady`
The original negative check (`not Resizing`) inadvertently allowed other states such as `Stopping` to pass through, which could cause the `EnableAutoScaleAsync` call to fail or produce unexpected behaviour. Switching to a positive match on `AllocationState.Steady` ensures the formula is only applied when the pool is in a known stable state.

### 4. Explicit evaluation interval: `autoScaleEvaluationInterval: TimeSpan.FromMinutes(15)`
The original call did not set an evaluation interval, relying on the Azure Batch default. The interval is now set explicitly to 15 minutes. This is intentional:
- The **orchestrator** drives scale-up by calling `EnableAutoScaleAsync` on every incoming request (every 5 minutes via the distributed lock).
- The **pool's own 15-minute self-evaluation** handles gradual scale-down during demand dips, avoiding unnecessary cold starts when load temporarily drops.

---

## `CloudPool pool Query Refined.cs` — Timezone-aware preload floor

Extends all changes in `CloudPool pool Query.cs` with additional logic to keep nodes warm ahead of predictable daily demand spikes.

### Background
Production data shows three recurring peak windows when designer activity causes sharp spikes in concurrent render jobs (up to 200–300 jobs):
- **Morning start** — designers beginning work
- **Pre-lunch** — activity surge before midday
- **Late afternoon** — end-of-day burst before close of business

Azure Batch autoscale formulas run server-side and have no access to local time, so the orchestrator injects these values at the point the formula is applied.

### Timezone resolution
The Windows timezone ID `"Central European Standard Time"` is used to compute the current local time. This covers:
- **Poland** — primary processing region
- **Italy** and **Spain** — overflow regions

All three countries share Central European Time (CET/CEST, UTC+1/UTC+2) and have synchronised DST transitions, so a single timezone ID is correct for all regions. No per-region adjustment is required.

### Peak window detection
```csharp
bool isPeakWindow = (localTime >= new TimeOnly(7, 45) && localTime < new TimeOnly(10, 0))    // morning start
                 || (localTime >= new TimeOnly(11, 30) && localTime < new TimeOnly(13, 0))    // pre-lunch
                 || (localTime >= new TimeOnly(15, 30) && localTime < new TimeOnly(17, 30));  // late afternoon
```
All times are in Central European local time. Window boundaries should be validated and tuned against Application Insights job-count data.

### Preload floor (`preloadMinJobs`)
During a peak window, 30% of the quota-based `maxJobs` cap is passed to the formula as a minimum node floor (`{1}`). Outside peak hours the value is `0`, allowing normal scale-down to proceed.

```csharp
int preloadMinJobs = isPeakWindow ? (int)Math.Ceiling(maxJobs * 0.30) : 0;
string formattedFormula = string.Format(this._scaleFormula, maxJobs, preloadMinJobs);
```

### Formula template update required
To use the preload floor, the Key Vault formula template must reference the `{1}` placeholder. Example:
```
$minNodes = {1} / $tasksPerNode;
$TargetDedicatedNodes = max($minNodes, min($runningTasks / $tasksPerNode, {0}));
```
Until the template is updated, `string.Format` silently ignores the extra argument — there is no runtime impact in the meantime.

### Future considerations
- The 30% floor and window boundaries should be tested against Application Insights data.
- If overflow regions ever expand outside Central Europe (e.g. a UK-only pool), the timezone logic will need revisiting as UK time diverges from CET during British Summer Time.
- During promotional periods, `maxJobs` is expected to be raised via Key Vault and pipeline, which automatically scales the preload floor proportionally without any code change.
