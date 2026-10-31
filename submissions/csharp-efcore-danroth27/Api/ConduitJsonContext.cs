using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Api;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(ProfileResponse))]
[JsonSerializable(typeof(ArticleResponse))]
[JsonSerializable(typeof(ArticlesResponse))]
[JsonSerializable(typeof(CommentResponse))]
[JsonSerializable(typeof(CommentsResponse))]
[JsonSerializable(typeof(TagsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(NewArticleRequest))]
[JsonSerializable(typeof(UpdateArticleRequest))]
[JsonSerializable(typeof(NewCommentRequest))]
internal partial class ConduitJsonContext : JsonSerializerContext;
