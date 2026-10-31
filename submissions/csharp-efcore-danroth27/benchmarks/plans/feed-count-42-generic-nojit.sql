DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_generic_plan; SET jit=off; SET max_parallel_workers_per_gather=0; PREPARE probe(bigint) AS SELECT count(*)::int
FROM (
    SELECT 1
    FROM follows AS f
    INNER JOIN articles AS a ON f.followee_id = a.author_id
    WHERE f.follower_id = $1
    LIMIT 1000
) AS s; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe(42);