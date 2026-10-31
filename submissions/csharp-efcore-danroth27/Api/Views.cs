using System.Text.Json.Serialization;
using Conduit.Data;

namespace Conduit.Api;

public record UserView(string Email, string Token, string Username, string? Bio, string? Image)
{
    public static UserView From(User user, string token) => new(user.Email, token, user.Username, user.Bio, user.Image);
}

public record ProfileView(string Username, string? Bio, string? Image, bool Following)
{
    public static ProfileView From(User user, bool following) => new(user.Username, user.Bio, user.Image, following);
}

public record ArticleView(
    string Slug,
    string Title,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body,
    string[] TagList,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Favorited,
    int FavoritesCount,
    ProfileView Author);

public record CommentView(long Id, DateTime CreatedAt, DateTime UpdatedAt, string Body, ProfileView Author)
{
    public static CommentView From(Comment comment, bool following) =>
        new(comment.Id, comment.CreatedAt, comment.UpdatedAt, comment.Body, ProfileView.From(comment.Author, following));
}
