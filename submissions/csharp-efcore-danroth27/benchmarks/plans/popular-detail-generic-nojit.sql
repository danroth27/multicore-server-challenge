DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_generic_plan; SET jit=off; SET max_parallel_workers_per_gather=0; PREPARE probe(bigint, text) AS SELECT a.slug, a.title, a.description, a.body, COALESCE((
    SELECT array_agg(s.name ORDER BY s.name)
    FROM (
        SELECT t.name, 1 AS "Key"
        FROM article_tags AS a0
        INNER JOIN tags AS t ON a0.tag_id = t.id
        WHERE a.id = a0.article_id
    ) AS s
    GROUP BY s."Key"
    LIMIT 1), ARRAY[]::text[]), a.created_at, a.updated_at, EXISTS (
    SELECT 1
    FROM favorites AS f
    WHERE a.id = f.article_id AND f.user_id = $1), (
    SELECT count(*)::int
    FROM favorites AS f0
    WHERE a.id = f0.article_id), u.username, u.bio, u.image, EXISTS (
    SELECT 1
    FROM follows AS f1
    WHERE u.id = f1.followee_id AND f1.follower_id = $1)
FROM articles AS a
INNER JOIN users AS u ON a.author_id = u.id
WHERE a.slug = $2
LIMIT 2; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe(42, 'vu-laze-jecen-picex-seatrowom-bios-dailer-stipaisa-57421');