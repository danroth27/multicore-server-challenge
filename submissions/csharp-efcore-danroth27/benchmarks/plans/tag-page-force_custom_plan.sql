DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_custom_plan; PREPARE probe(bigint, text, integer, integer) AS SELECT a.slug, a.title, a.description, NULL, COALESCE((
    SELECT array_agg(s.name ORDER BY s.name)
    FROM (
        SELECT t0.name, 1 AS "Key"
        FROM article_tags AS a1
        INNER JOIN tags AS t0 ON a1.tag_id = t0.id
        WHERE a.id = a1.article_id
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
WHERE a.id IN (
    SELECT a0.article_id
    FROM article_tags AS a0
    WHERE a0.tag_id IN (
        SELECT t.id
        FROM tags AS t
        WHERE t.name = $2
    )
    ORDER BY a0.article_id DESC
    LIMIT $3 OFFSET $4
)
ORDER BY a.id DESC; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe(42, 'stun', 10, 0);