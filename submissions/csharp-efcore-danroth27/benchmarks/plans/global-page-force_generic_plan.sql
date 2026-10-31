DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_generic_plan; PREPARE probe(bigint, integer, integer) AS SELECT a0.slug, a0.title, a0.description, NULL, COALESCE((
    SELECT array_agg(s.name ORDER BY s.name)
    FROM (
        SELECT t.name, 1 AS "Key"
        FROM article_tags AS a1
        INNER JOIN tags AS t ON a1.tag_id = t.id
        WHERE a0.id = a1.article_id
    ) AS s
    GROUP BY s."Key"
    LIMIT 1), ARRAY[]::text[]), a0.created_at, a0.updated_at, EXISTS (
    SELECT 1
    FROM favorites AS f
    WHERE a0.id = f.article_id AND f.user_id = $1), (
    SELECT count(*)::int
    FROM favorites AS f0
    WHERE a0.id = f0.article_id), u.username, u.bio, u.image, EXISTS (
    SELECT 1
    FROM follows AS f1
    WHERE u.id = f1.followee_id AND f1.follower_id = $1)
FROM (
    SELECT a.id, a.author_id, a.created_at, a.description, a.slug, a.title, a.updated_at
    FROM articles AS a
    ORDER BY a.id DESC
    LIMIT $2 OFFSET $3
) AS a0
INNER JOIN users AS u ON a0.author_id = u.id
ORDER BY a0.id DESC; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe(42, 10, 0);