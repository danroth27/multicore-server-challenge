using System.Linq.Expressions;
using Conduit.Data;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

internal static class ReadQueries
{
    const int MaxCount = 1000;

    sealed class ArticleSource
    {
        public required Article Article { get; init; }
        public long ViewerId { get; init; }
        public bool IncludeBody { get; init; }
        public bool? Following { get; init; }
    }

    static readonly Expression<Func<ArticleSource, ArticleView>> View = source => new(
        source.Article.Slug, source.Article.Title, source.Article.Description,
        source.IncludeBody ? source.Article.Body : null,
        source.Article.Tags.GroupBy(t => 1)
            .Select(g => EF.Functions.ArrayAgg(g.OrderBy(t => t.Name).Select(t => t.Name)))
            .FirstOrDefault() ?? Array.Empty<string>(),
        source.Article.CreatedAt, source.Article.UpdatedAt,
        source.Article.Favorites.Any(f => f.UserId == source.ViewerId),
        source.Article.Favorites.Count,
        new ProfileView(source.Article.Author.Username, source.Article.Author.Bio, source.Article.Author.Image,
            source.Following ?? source.Article.Author.Followers.Any(f => f.FollowerId == source.ViewerId)));

    public static readonly Func<ConduitDb, string, long, CancellationToken, Task<ArticleView?>> ArticleBySlug =
        EF.CompileAsyncQuery((ConduitDb db, string slug, long viewer, CancellationToken ct) =>
            db.Articles.Where(a => a.Slug == slug)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = true, Following = null })
                .Select(View).SingleOrDefault());

    public static readonly Func<ConduitDb, long, long, CancellationToken, Task<ArticleView>> ArticleById =
        EF.CompileAsyncQuery((ConduitDb db, long id, long viewer, CancellationToken ct) =>
            db.Articles.Where(a => a.Id == id)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = true, Following = null })
                .Select(View).Single());

    public static readonly Func<ConduitDb, long, int, int, IAsyncEnumerable<ArticleView>> GlobalPage =
        EF.CompileAsyncQuery((ConduitDb db, long viewer, int limit, int offset) =>
            db.Articles.OrderByDescending(a => a.Id).Skip(offset).Take(limit)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = false, Following = null })
                .Select(View));

    public static readonly Func<ConduitDb, string, long, int, int, IAsyncEnumerable<ArticleView>> TagPage =
        EF.CompileAsyncQuery((ConduitDb db, string tag, long viewer, int limit, int offset) =>
            db.Articles.Where(a => db.ArticleTags
                    .Where(at => db.Tags.Where(t => t.Name == tag).Select(t => t.Id).Contains(at.TagId))
                    .OrderByDescending(at => at.ArticleId).Skip(offset).Take(limit).Select(at => at.ArticleId).Contains(a.Id))
                .OrderByDescending(a => a.Id)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = false, Following = null })
                .Select(View));

    public static readonly Func<ConduitDb, string, long, int, int, IAsyncEnumerable<ArticleView>> AuthorPage =
        EF.CompileAsyncQuery((ConduitDb db, string author, long viewer, int limit, int offset) =>
            db.Articles.Where(a => db.Articles
                    .Where(p => db.Users.Where(u => u.Username == author).Select(u => u.Id).Contains(p.AuthorId))
                    .OrderByDescending(p => p.Id).Skip(offset).Take(limit).Select(p => p.Id).Contains(a.Id))
                .OrderByDescending(a => a.Id)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = false, Following = null })
                .Select(View));

    public static readonly Func<ConduitDb, string, long, int, int, IAsyncEnumerable<ArticleView>> FavoritedPage =
        EF.CompileAsyncQuery((ConduitDb db, string username, long viewer, int limit, int offset) =>
            db.Articles.Where(a => db.Favorites
                    .Where(f => db.Users.Where(u => u.Username == username).Select(u => u.Id).Contains(f.UserId))
                    .OrderByDescending(f => f.ArticleId).Skip(offset).Take(limit).Select(f => f.ArticleId).Contains(a.Id))
                .OrderByDescending(a => a.Id)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = false, Following = null })
                .Select(View));

    // Carry follower context through the page: Npgsql then uses LATERAL rather than ROW_NUMBER over all articles.
    public static readonly Func<ConduitDb, long, int, int, int, IAsyncEnumerable<ArticleView>> FeedPage =
        EF.CompileAsyncQuery((ConduitDb db, long viewer, int budget, int limit, int offset) =>
            db.Follows.Where(f => f.FollowerId == viewer)
                .SelectMany(f => db.Articles.Where(a => a.AuthorId == f.FolloweeId)
                    .OrderByDescending(a => a.Id).Take(budget).Select(a => new { a.Id, f.FollowerId }))
                .OrderByDescending(a => a.Id).Skip(offset).Take(limit)
                .Join(db.Articles, p => p.Id, a => a.Id,
                    (p, a) => new ArticleSource { Article = a, ViewerId = p.FollowerId, IncludeBody = false, Following = true })
                .OrderByDescending(a => a.Article.Id)
                .Select(View));

    public static readonly Func<ConduitDb, long, int, int, IAsyncEnumerable<ArticleView>> UnboundedFeedPage =
        EF.CompileAsyncQuery((ConduitDb db, long viewer, int limit, int offset) =>
            db.Follows.Where(f => f.FollowerId == viewer)
                .SelectMany(f => db.Articles.Where(a => a.AuthorId == f.FolloweeId))
                .OrderByDescending(a => a.Id).Skip(offset).Take(limit)
                .Select(a => new ArticleSource { Article = a, ViewerId = viewer, IncludeBody = false, Following = true })
                .Select(View));

    public static readonly Func<ConduitDb, CancellationToken, Task<int>> GlobalCount =
        EF.CompileAsyncQuery((ConduitDb db, CancellationToken ct) =>
            db.Articles.OrderBy(a => a.Id).Take(MaxCount).Count());

    public static readonly Func<ConduitDb, string, CancellationToken, Task<int>> TagCount =
        EF.CompileAsyncQuery((ConduitDb db, string tag, CancellationToken ct) =>
            db.ArticleTags.Where(at => db.Tags.Where(t => t.Name == tag).Select(t => t.Id).Contains(at.TagId))
                .Take(MaxCount).Count());

    public static readonly Func<ConduitDb, string, CancellationToken, Task<int>> AuthorCount =
        EF.CompileAsyncQuery((ConduitDb db, string author, CancellationToken ct) =>
            db.Articles.Where(a => db.Users.Where(u => u.Username == author).Select(u => u.Id).Contains(a.AuthorId))
                .Take(MaxCount).Count());

    public static readonly Func<ConduitDb, string, CancellationToken, Task<int>> FavoritedCount =
        EF.CompileAsyncQuery((ConduitDb db, string username, CancellationToken ct) =>
            db.Favorites.Where(f => db.Users.Where(u => u.Username == username).Select(u => u.Id).Contains(f.UserId))
                .Take(MaxCount).Count());

    public static readonly Func<ConduitDb, long, CancellationToken, Task<int>> FeedCount =
        EF.CompileAsyncQuery((ConduitDb db, long viewer, CancellationToken ct) =>
            db.Follows.Where(f => f.FollowerId == viewer)
                .SelectMany(f => db.Articles.Where(a => a.AuthorId == f.FolloweeId)).Take(MaxCount).Count());

    public static readonly Func<ConduitDb, string, long, CancellationToken, Task<ProfileView?>> Profile =
        EF.CompileAsyncQuery((ConduitDb db, string username, long viewer, CancellationToken ct) =>
            db.Users.Where(u => u.Username == username)
                .Select(u => new ProfileView(u.Username, u.Bio, u.Image, u.Followers.Any(f => f.FollowerId == viewer)))
                .SingleOrDefault());

    public static readonly Func<ConduitDb, string, long, CancellationToken, Task<CommentsResponse?>> Comments =
        EF.CompileAsyncQuery((ConduitDb db, string slug, long viewer, CancellationToken ct) =>
            db.Articles.Where(a => a.Slug == slug)
                .Select(a => new CommentsResponse(db.Comments.Where(c => c.ArticleId == a.Id)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(100)
                    .Select(c => new CommentView(c.Id, c.CreatedAt, c.UpdatedAt, c.Body,
                        new ProfileView(c.Author.Username, c.Author.Bio, c.Author.Image,
                            c.Author.Followers.Any(f => f.FollowerId == viewer))))
                    .ToList()))
                .SingleOrDefault());

    public static readonly Func<ConduitDb, IAsyncEnumerable<string>> Tags =
        EF.CompileAsyncQuery((ConduitDb db) => db.Tags.OrderBy(t => t.Name).Select(t => t.Name));
}
