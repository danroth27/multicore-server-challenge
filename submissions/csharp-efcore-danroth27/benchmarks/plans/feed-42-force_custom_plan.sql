DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_custom_plan; PREPARE probe(integer, bigint, integer, integer) AS SELECT a1.slug, a1.title, a1.description, NULL, COALESCE((
    SELECT array_agg(s0.name ORDER BY s0.name)
    FROM (
        SELECT t.name, 1 AS "Key"
        FROM article_tags AS a2
        INNER JOIN tags AS t ON a2.tag_id = t.id
        WHERE a1.id = a2.article_id
    ) AS s0
    GROUP BY s0."Key"
    LIMIT 1), ARRAY[]::text[]), a1.created_at, a1.updated_at, EXISTS (
    SELECT 1
    FROM favorites AS f0
    WHERE a1.id = f0.article_id AND f0.user_id = s."FollowerId"), (
    SELECT count(*)::int
    FROM favorites AS f1
    WHERE a1.id = f1.article_id), u.username, u.bio, u.image, TRUE
FROM (
    SELECT a0."Id", a0."FollowerId"
    FROM follows AS f
    JOIN LATERAL (
        SELECT a.id AS "Id", f.follower_id AS "FollowerId"
        FROM articles AS a
        WHERE a.author_id = f.followee_id
        ORDER BY a.id DESC
        LIMIT $1
    ) AS a0 ON TRUE
    WHERE f.follower_id = $2
    ORDER BY a0."Id" DESC
    LIMIT $3 OFFSET $4
) AS s
INNER JOIN articles AS a1 ON s."Id" = a1.id
INNER JOIN users AS u ON a1.author_id = u.id
ORDER BY a1.id DESC; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe(10, 42, 10, 0);