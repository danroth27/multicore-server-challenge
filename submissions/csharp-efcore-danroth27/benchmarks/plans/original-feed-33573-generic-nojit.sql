DEALLOCATE ALL; RESET jit; RESET max_parallel_workers_per_gather; SET plan_cache_mode=force_generic_plan; SET jit=off; SET max_parallel_workers_per_gather=0; PREPARE probe(bigint, integer, integer) AS SELECT s.slug, s.title, s.description, s.follower_id, s.followee_id, s.id, s.id0, u0.id, s0.name, s0.article_id, s0.tag_id, s0.id, s.created_at, s.updated_at, EXISTS (
    SELECT 1
    FROM favorites AS f0
    WHERE s.id0 = f0.article_id AND f0.user_id = $1), (
    SELECT count(*)::int
    FROM favorites AS f1
    WHERE s.id0 = f1.article_id), u0.username, u0.bio, u0.image, EXISTS (
    SELECT 1
    FROM follows AS f2
    WHERE u0.id = f2.followee_id AND f2.follower_id = $1)
FROM (
    SELECT f.follower_id, f.followee_id, u.id, a.id AS id0, a.author_id, a.created_at, a.description, a.slug, a.title, a.updated_at
    FROM follows AS f
    INNER JOIN users AS u ON f.followee_id = u.id
    INNER JOIN articles AS a ON u.id = a.author_id
    WHERE f.follower_id = $1
    ORDER BY a.id DESC
    LIMIT $2 OFFSET $3
) AS s
INNER JOIN users AS u0 ON s.author_id = u0.id
LEFT JOIN (
    SELECT t.name, a0.article_id, a0.tag_id, t.id
    FROM article_tags AS a0
    INNER JOIN tags AS t ON a0.tag_id = t.id
) AS s0 ON s.id0 = s0.article_id
ORDER BY s.id0 DESC, s.follower_id, s.followee_id, s.id, u0.id, s0.name, s0.article_id, s0.tag_id; EXPLAIN (ANALYZE, BUFFERS, SETTINGS, FORMAT JSON) EXECUTE probe(33573, 10, 0);