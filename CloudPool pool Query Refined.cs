CloudPool pool = await client.PoolOperations.GetPoolAsync(
    this._settings.BatchPoolId,
    detailLevel: new ODATADetailLevel(selectClause: "id,allocationState,autoScaleFormula,autoScaleRun"),
    additionalBehaviors: BatchClientRetryPolicies);

// Only proceed once the previous evaluation has fully propagated (5-minute orchestrator window).
// The distributed lock ensures only one instance reaches this point concurrently, so the
// rare propagation-delay errors are limited to the window between lock release and pool state sync.
bool passedCooldown = pool.AutoScaleRun?.Timestamp is not { } lastRun
                      || DateTime.UtcNow - lastRun.ToUniversalTime() >= TimeSpan.FromMinutes(5);

if (pool.AllocationState is AllocationState.Steady && passedCooldown)
{
    // Compute a timezone-aware preload floor so the pool keeps nodes warm ahead of
    // predictable demand spikes. Poland is the primary processing region with overflow
    // to Italy and Spain — all three share Central European Time (CET/CEST, UTC+1/UTC+2),
    // so a single timezone ID covers all regions. DST transitions are also synchronised
    // across all three countries, so no per-region adjustment is needed.
    TimeZoneInfo polandTz = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    TimeOnly localTime = TimeOnly.FromTimeSpan(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, polandTz).TimeOfDay);

    // Three recurring peak windows observed in production: designers arriving in the
    // morning, a pre-lunch spike, and a late-afternoon burst before end of day.
    // Thresholds should be tuned against Application Insights job-count data.
    bool isPeakWindow = (localTime >= new TimeOnly(7, 45) && localTime < new TimeOnly(10, 0))    // morning start
                     || (localTime >= new TimeOnly(11, 30) && localTime < new TimeOnly(13, 0))    // pre-lunch
                     || (localTime >= new TimeOnly(15, 30) && localTime < new TimeOnly(17, 30));  // late afternoon

    // During peak windows, hold ~30 % of the quota-based cap as a warm floor to avoid
    // cold-start delays when demand spikes sharply. Outside peak hours the floor is 0
    // so the pool can scale down as normal. Injected as {1} in the formula template;
    // string.Format ignores it safely if the template does not yet reference {1}.
    int preloadMinJobs = isPeakWindow ? (int)Math.Ceiling(maxJobs * 0.30) : 0;

    string formattedFormula = string.Format(this._scaleFormula, maxJobs, preloadMinJobs);

    // Evaluation interval is kept at 15 minutes: the orchestrator drives scale-up via the
    // 5-minute window on every request, while the pool's own 15-minute self-evaluation
    // handles gradual scale-down during demand dips without triggering cold starts.
    await client.PoolOperations.EnableAutoScaleAsync(
        this._settings.BatchPoolId,
        formattedFormula,
        autoScaleEvaluationInterval: TimeSpan.FromMinutes(15),
        additionalBehaviors: BatchClientRetryPolicies);
}
