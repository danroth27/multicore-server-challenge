DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_generic_plan; PREPARE probe AS SELECT count(*)::int
FROM (
    SELECT 1
    FROM articles AS a
    ORDER BY a.id
    LIMIT 1000
) AS a0; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe;