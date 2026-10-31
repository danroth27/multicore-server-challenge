DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_generic_plan; PREPARE probe(text, bigint) AS SELECT a0.id, s.id, s.created_at, s.updated_at, s.body, s.username, s.bio, s.image, s.c, s.id0
FROM (
    SELECT a.id
    FROM articles AS a
    WHERE a.slug = $1
    LIMIT 2
) AS a0
LEFT JOIN LATERAL (
    SELECT c0.id, c0.created_at, c0.updated_at, c0.body, u.username, u.bio, u.image, EXISTS (
        SELECT 1
        FROM follows AS f
        WHERE u.id = f.followee_id AND f.follower_id = $2) AS c, u.id AS id0
    FROM (
        SELECT c.id, c.author_id, c.body, c.created_at, c.updated_at
        FROM comments AS c
        WHERE c.article_id = a0.id
        ORDER BY c.created_at DESC, c.id DESC
        LIMIT 100
    ) AS c0
    INNER JOIN users AS u ON c0.author_id = u.id
) AS s ON TRUE
ORDER BY a0.id, s.created_at DESC, s.id DESC; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe('vu-laze-jecen-picex-seatrowom-bios-dailer-stipaisa-57421', 42);