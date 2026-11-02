using Conduit.Auth;
using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class ArticlesApi
{
    public static RouteGroupBuilder MapArticlesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/articles", List).AuthOptional();
        api.MapGet("/articles/feed", Feed).AuthRequired();
        api.MapGet("/articles/{slug}", Get).AuthOptional();
        api.MapPost("/articles", Create).AuthRequired();
        api.MapPut("/articles/{slug}", Update).AuthRequired();
        api.MapDelete("/articles/{slug}", Delete).AuthRequired();
        api.MapPost("/articles/{slug}/favorite", Favorite).AuthRequired();
        api.MapDelete("/articles/{slug}/favorite", Unfavorite).AuthRequired();
        return api;
    }

    static async Task<Ok<ArticlesResponse>> List(
        [AsParameters] ArticleFilter filter, Page page, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        (IAsyncEnumerable<ArticleView> Rows, string Key, Func<ConduitDb, string, CancellationToken, Task<int>>? Count) query =
            filter switch
            {
                { Tag: { Length: > 0 } tag } =>
                    (ReadQueries.TagPage(db, tag, viewer.Id, page.Limit, page.Offset), tag, ReadQueries.TagCount),
                { Author: { Length: > 0 } author } =>
                    (ReadQueries.AuthorPage(db, author, viewer.Id, page.Limit, page.Offset), author, ReadQueries.AuthorCount),
                { Favorited: { Length: > 0 } username } =>
                    (ReadQueries.FavoritedPage(db, username, viewer.Id, page.Limit, page.Offset), username, ReadQueries.FavoritedCount),
                _ => (ReadQueries.GlobalPage(db, viewer.Id, page.Limit, page.Offset), "", null)
            };
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var articles = await query.Rows.ToListAsync(ct);
            var count = query.Count is null ? await ReadQueries.GlobalCount(db, ct) : await query.Count(db, query.Key, ct);
            return TypedResults.Ok(new ArticlesResponse(articles, count));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    static async Task<Ok<ArticlesResponse>> Feed(Page page, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var rows = page.Offset > int.MaxValue - page.Limit
            ? ReadQueries.UnboundedFeedPage(db, viewer.Id, page.Limit, page.Offset)
            : ReadQueries.FeedPage(db, viewer.Id, page.Limit + page.Offset, page.Limit, page.Offset);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var articles = await rows.ToListAsync(ct);
            var count = await ReadQueries.FeedCount(db, viewer.Id, ct);
            return TypedResults.Ok(new ArticlesResponse(articles, count));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    static async Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Get(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var article = await ReadQueries.ArticleBySlug(db, slug, viewer.Id, ct);
        if (article is null) return Errors.NotFound;

        return TypedResults.Ok(new ArticleResponse(article));
    }

    static async Task<Created<ArticleResponse>> Create(NewArticleRequest request, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var draft = request.Article;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var article = new Article
        {
            AuthorId = viewer.Id,
            Description = draft.Description,
            Body = draft.Body,
            Tags = await db.TagsNamedAsync(draft.TagList ?? [], ct),
        };
        await db.Articles.AddAsync(article, ct); // gives the article its id
        article.SetTitle(draft.Title);           // the slug ends with that id
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var view = await Find(db, article.Id, viewer, ct);
        return TypedResults.Created($"/api/articles/{article.Slug}", new ArticleResponse(view));
    }

    static async Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Update(
        string slug, UpdateArticleRequest request, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var article = await db.Articles.SingleOrDefaultAsync(a => a.Slug == slug, ct);
        if (article is null) return Errors.NotFound;
        if (article.AuthorId != viewer.Id) return Errors.Forbidden;

        request.Article.ApplyTo(article);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new ArticleResponse(await Find(db, article.Id, viewer, ct)));
    }

    static async Task<Results<NoContent, ProblemHttpResult>> Delete(string slug, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var article = await db.Articles.Where(a => a.Slug == slug)
            .Select(a => new { a.Id, a.AuthorId }).SingleOrDefaultAsync(ct);
        if (article is null) return Errors.NotFound;
        if (article.AuthorId != viewer.Id) return Errors.Forbidden;

        await db.Articles.Where(a => a.Id == article.Id && a.AuthorId == viewer.Id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    static Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Favorite(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        ChangeFavorite(slug, favorite: true, viewer, db, ct);

    static Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Unfavorite(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        ChangeFavorite(slug, favorite: false, viewer, db, ct);

    static async Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> ChangeFavorite(
        string slug, bool favorite, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var articleId = await db.ArticleIdAsync(slug, ct);
        if (articleId is null) return Errors.NotFound;

        if (favorite) await db.FavoriteAsync(viewer.Id, articleId.Value, ct);
        else await db.UnfavoriteAsync(viewer.Id, articleId.Value, ct);

        return TypedResults.Ok(new ArticleResponse(await Find(db, articleId.Value, viewer, ct)));
    }

    static Task<ArticleView> Find(ConduitDb db, long id, Viewer viewer, CancellationToken ct) =>
        ReadQueries.ArticleById(db, id, viewer.Id, ct);
}
