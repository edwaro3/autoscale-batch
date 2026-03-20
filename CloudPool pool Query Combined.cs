CloudPool pool = await client.PoolOperations.GetPoolAsync(
    this._settings.BatchPoolId,
    detailLevel: new ODATADetailLevel(selectClause: "id,allocationState,autoScaleFormula,autoScaleRun"),
    additionalBehaviors: BatchClientRetryPolicies);

// Use the orchestrator's own last-write timestamp rather than AutoScaleRun.Timestamp.
// AutoScaleRun.Timestamp reflects Azure Batch's 15-minute evaluation cadence, not when
// the orchestrator last called EnableAutoScaleAsync — using it causes false positives.

DateTimeOffset? lastScaleWrite = await this._scaleLockStore.GetLastWriteTimeAsync(this._settings.BatchPoolId);

bool passedCooldown = lastScaleWrite is null
                      || DateTimeOffset.UtcNow - lastScaleWrite.Value >= TimeSpan.FromMinutes(5);

if (pool.AllocationState is AllocationState.Steady && passedCooldown)
{
    // Poland is the primary region; Italy and Spain overflow. All three share CET/CEST
    // (UTC+1/UTC+2) with synchronised DST — one timezone ID covers all regions.
    
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

    // Record write time so subsequent requests within the 5-minute window
    // see an accurate cooldown timestamp, not a stale pool evaluation time.
    
    await this._scaleLockStore.SetLastWriteTimeAsync(this._settings.BatchPoolId, DateTimeOffset.UtcNow);
}
