using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Conduit.Data;

public class ConduitDb(DbContextOptions<ConduitDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ArticleTag> ArticleTags => Set<ArticleTag>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Comment> Comments => Set<Comment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Follow>().HasKey(f => new { f.FollowerId, f.FolloweeId });
        model.Entity<Follow>().HasOne(f => f.Followee).WithMany(u => u.Followers).HasForeignKey(f => f.FolloweeId);

        model.Entity<Favorite>().HasKey(f => new { f.UserId, f.ArticleId });

        // The slug ends with the id, so EF takes the id from the column's own sequence when the article is added.
        model.HasSequence<long>("articles_id_seq").IncrementsBy(1);
        model.Entity<Article>().Property(a => a.Id).UseHiLo("articles_id_seq");

        model.Entity<Article>().HasMany(a => a.Tags).WithMany(t => t.Articles).UsingEntity<ArticleTag>(
            tag => tag.HasOne<Tag>().WithMany().HasForeignKey(t => t.TagId),
            article => article.HasOne<Article>().WithMany().HasForeignKey(t => t.ArticleId),
            link => link.ToTable("article_tags").HasKey(t => new { t.ArticleId, t.TagId }));
    }

    public Task<long?> ArticleIdAsync(string slug, CancellationToken ct) =>
        Articles.Where(a => a.Slug == slug).Select(a => (long?)a.Id).SingleOrDefaultAsync(ct);

    /// <summary>Returns the tags with these names, creating the missing ones.</summary>
    public async Task<List<Tag>> TagsNamedAsync(string[] names, CancellationToken ct)
    {
        if (names.Length == 0) return [];

        var tags = await Tags.Where(t => names.Contains(t.Name)).ToListAsync(ct);
        // Keep tag insertion lock order consistent across concurrent requests.
        var missing = names.Except(tags.Select(t => t.Name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return tags;

        await Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO tags (name) SELECT unnest({missing}) ON CONFLICT (name) DO NOTHING", ct);
        return await Tags.Where(t => names.Contains(t.Name)).ToListAsync(ct);
    }

    public Task FollowAsync(long followerId, long followeeId, CancellationToken ct) =>
        Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO follows (follower_id, followee_id) VALUES ({followerId}, {followeeId}) ON CONFLICT (follower_id, followee_id) DO NOTHING", ct);

    public Task UnfollowAsync(long followerId, long followeeId, CancellationToken ct) =>
        Follows.Where(f => f.FollowerId == followerId && f.FolloweeId == followeeId).ExecuteDeleteAsync(ct);

    public Task FavoriteAsync(long userId, long articleId, CancellationToken ct) =>
        Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO favorites (user_id, article_id) VALUES ({userId}, {articleId}) ON CONFLICT (user_id, article_id) DO NOTHING", ct);

    public Task UnfavoriteAsync(long userId, long articleId, CancellationToken ct) =>
        Favorites.Where(f => f.UserId == userId && f.ArticleId == articleId).ExecuteDeleteAsync(ct);

}

public static class DbUpdateExceptionExtensions
{
    public static bool IsUniqueViolation(this DbUpdateException error) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

public static class ConduitDbSetup
{
    public static IServiceCollection AddConduitDb(this IServiceCollection services, string databaseUrl, IConfiguration configuration)
    {
        var connectionString = ConnectionString(databaseUrl, configuration);
        return services.AddDbContextPool<ConduitDb>(db => db
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            // The exception handler logs unexpected failures and translates duplicate usernames/emails to 422.
            .ConfigureWarnings(log => log.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));
    }

    /// <summary>Turns postgres://user:password@host:port/database into an Npgsql connection string.</summary>
    static string ConnectionString(string databaseUrl, IConfiguration configuration)
    {
        var url = new Uri(databaseUrl);
        if (url.Scheme is not ("postgres" or "postgresql"))
            throw new ArgumentException("DATABASE_URL must use the postgres or postgresql scheme.", nameof(databaseUrl));
        if (url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException(
                "DATABASE_URL query parameters and fragments are unsupported; refusing to ignore connection or security settings.",
                nameof(databaseUrl));
        var credentials = url.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        return new NpgsqlConnectionStringBuilder
        {
            Host = url.Host,
            Port = url.IsDefaultPort ? 5432 : url.Port,
            Database = url.AbsolutePath.Trim('/'),
            Username = credentials[0],
            Password = credentials.ElementAtOrDefault(1),
            MaxPoolSize = configuration.GetValue("DB_POOL_SIZE", 32),
            MaxAutoPrepare = configuration.GetValue("DB_MAX_AUTO_PREPARE", 64),
            AutoPrepareMinUsages = configuration.GetValue("DB_AUTO_PREPARE_MIN_USAGES", 2),
            Options = configuration["DB_OPTIONS"],
        }.ConnectionString;
    }
}
