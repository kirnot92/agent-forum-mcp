using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForum.Server.Domain;
using AgentForum.Server.McpTools;
using AgentForum.Server.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentForum.Server.Tests.McpTools;

public sealed class ForumToolsContractTests
{
    private static readonly NullabilityInfoContext Nullability = new();

    [Fact]
    public void AdapterIsDiscoverableAndConstructorInjectsForumService()
    {
        Assert.NotNull(typeof(ForumTools).GetCustomAttribute<McpServerToolTypeAttribute>());

        var constructor = Assert.Single(typeof(ForumTools).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(ForumService), parameter.ParameterType);
        Assert.Equal("forumService", parameter.Name);
    }

    [Fact]
    public void ExposesExactlySevenAttributedToolsWithExpectedDescriptionsAndAnnotations()
    {
        var methods = ToolMethods();

        Assert.Equal(ToolContract.ToolNames.Order(), methods.Keys.Order());
        Assert.Equal(ToolContract.CreatePostDescription, Description(methods["create_post"]));
        Assert.Equal(SearchPostsDescription, Description(methods["search_posts"]));
        Assert.Equal(ReadPostDescription, Description(methods["read_post"]));
        Assert.Equal(CreateCommentDescription, Description(methods["create_comment"]));
        Assert.Equal(ReadCommentsDescription, Description(methods["read_comments"]));
        Assert.Equal(VotePostDescription, Description(methods["vote_post"]));
        Assert.Equal(VerifyPostDescription, Description(methods["verify_post"]));

        foreach (var (name, method) in methods)
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(name, attribute.Name);
            Assert.False(attribute.Destructive);
            Assert.False(attribute.OpenWorld);
            Assert.Equal(IsReadOnly(name), attribute.ReadOnly);

            var protocolTool = CreateProtocolTool(method);
            Assert.Equal(name, protocolTool.Name);
            Assert.Equal(Description(method), protocolTool.Description);
            Assert.NotNull(protocolTool.Annotations);
            Assert.Equal(IsReadOnly(name), protocolTool.Annotations.ReadOnlyHint);
            Assert.False(protocolTool.Annotations.DestructiveHint);
            Assert.False(protocolTool.Annotations.OpenWorldHint);
        }
    }

    [Fact]
    public void MethodsHaveTheExactServiceAdapterSignatures()
    {
        var methods = ToolMethods();

        AssertMethod(
            methods["create_post"],
            typeof(Task<Post>),
            ("repo", typeof(string), false, null, NullabilityState.NotNull, ToolContract.RepoDescription),
            ("title", typeof(string), false, null, NullabilityState.NotNull, ToolContract.TitleDescription),
            ("content", typeof(string), false, null, NullabilityState.NotNull, ToolContract.ContentDescription),
            ("branch", typeof(string), false, null, NullabilityState.NotNull, ToolContract.BranchDescription),
            ("commit", typeof(string), false, null, NullabilityState.NotNull, ToolContract.CommitDescription),
            ("agent", typeof(string), true, null, NullabilityState.Nullable, ToolContract.AgentDescription),
            ("model", typeof(string), true, null, NullabilityState.Nullable, ToolContract.ModelDescription),
            ("effort", typeof(string), true, null, NullabilityState.Nullable, ToolContract.EffortDescription));

        AssertMethod(
            methods["search_posts"],
            typeof(Task<IReadOnlyList<PostSearchResult>>),
            ("repo", typeof(string), false, null, NullabilityState.NotNull, ToolContract.RepoDescription),
            ("query", typeof(string), false, null, NullabilityState.NotNull, SearchQueryDescription),
            ("limit", typeof(int), true, 10, NullabilityState.NotNull, SearchLimitDescription));

        AssertMethod(
            methods["read_post"],
            typeof(Task<ReadPostResult>),
            ("post_id", typeof(long), false, null, NullabilityState.NotNull, ReadPostIdDescription));

        AssertMethod(
            methods["create_comment"],
            typeof(Task<Comment>),
            ("post_id", typeof(long), false, null, NullabilityState.NotNull, CommentPostIdDescription),
            ("content", typeof(string), false, null, NullabilityState.NotNull, CommentContentDescription),
            ("branch", typeof(string), false, null, NullabilityState.NotNull, ToolContract.BranchDescription),
            ("commit", typeof(string), false, null, NullabilityState.NotNull, ToolContract.CommitDescription),
            ("agent", typeof(string), true, null, NullabilityState.Nullable, ToolContract.AgentDescription),
            ("model", typeof(string), true, null, NullabilityState.Nullable, ToolContract.ModelDescription),
            ("effort", typeof(string), true, null, NullabilityState.Nullable, ToolContract.EffortDescription));

        AssertMethod(
            methods["read_comments"],
            typeof(Task<ReadCommentsResult>),
            ("post_id", typeof(long), false, null, NullabilityState.NotNull, ReadCommentsPostIdDescription),
            ("limit", typeof(int), true, 20, NullabilityState.NotNull, CommentLimitDescription),
            ("offset", typeof(int), true, 0, NullabilityState.NotNull, CommentOffsetDescription));

        AssertMethod(
            methods["vote_post"],
            typeof(Task<Vote>),
            ("post_id", typeof(long), false, null, NullabilityState.NotNull, VotePostIdDescription),
            ("value", typeof(int), false, null, NullabilityState.NotNull, VoteValueDescription),
            ("agent", typeof(string), true, null, NullabilityState.Nullable, ToolContract.AgentDescription),
            ("model", typeof(string), true, null, NullabilityState.Nullable, ToolContract.ModelDescription));

        AssertMethod(
            methods["verify_post"],
            typeof(Task<Verification>),
            ("post_id", typeof(long), false, null, NullabilityState.NotNull, VerifyPostIdDescription),
            ("outcome", typeof(VerificationOutcome), false, null, NullabilityState.NotNull, VerificationOutcomeDescription),
            ("branch", typeof(string), false, null, NullabilityState.NotNull, ToolContract.BranchDescription),
            ("commit", typeof(string), false, null, NullabilityState.NotNull, ToolContract.CommitDescription),
            ("note", typeof(string), true, null, NullabilityState.Nullable, VerificationNoteDescription),
            ("agent", typeof(string), true, null, NullabilityState.Nullable, ToolContract.AgentDescription),
            ("model", typeof(string), true, null, NullabilityState.Nullable, ToolContract.ModelDescription),
            ("effort", typeof(string), true, null, NullabilityState.Nullable, ToolContract.EffortDescription));
    }

    [Fact]
    public void FactorySchemasExposeOnlyToolArgumentsWithDescriptionsDefaultsAndNullability()
    {
        foreach (var method in ToolMethods().Values)
        {
            var parameters = method.GetParameters();
            var toolParameters = parameters[..^1];
            var schema = CreateProtocolTool(method).InputSchema;
            var properties = schema.GetProperty("properties");
            var required = schema.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToHashSet()
                : [];

            Assert.Equal(toolParameters.Select(parameter => parameter.Name), PropertyNames(properties));
            Assert.False(properties.TryGetProperty("cancellationToken", out _));

            foreach (var parameter in toolParameters)
            {
                var property = properties.GetProperty(parameter.Name!);
                Assert.Equal(Description(parameter), property.GetProperty("description").GetString());
                Assert.Equal(!parameter.HasDefaultValue, required.Contains(parameter.Name));

                if (parameter.HasDefaultValue)
                {
                    Assert.True(property.TryGetProperty("default", out var defaultValue));
                    Assert.Equal(
                        JsonSerializer.Serialize(parameter.DefaultValue),
                        defaultValue.GetRawText());
                }

                Assert.Equal(
                    Nullability.Create(parameter).ReadState == NullabilityState.Nullable,
                    AllowsNull(property));
            }
        }

        var verifySchema = CreateProtocolTool(ToolMethods()["verify_post"]).InputSchema;
        var verifyRequired = verifySchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.DoesNotContain("note", verifyRequired);
        Assert.True(AllowsNull(verifySchema.GetProperty("properties").GetProperty("note")));
    }

    [Fact]
    public void ProvenanceDescriptionsRequireExactExplicitRuntimeValues()
    {
        Assert.Equal(ExpectedModelDescription, ToolContract.ModelDescription);
        Assert.Equal(ExpectedEffortDescription, ToolContract.EffortDescription);

        Assert.Contains("coding-agent model", ToolContract.ModelDescription, StringComparison.Ordinal);
        Assert.Contains("not the forum's embedding model", ToolContract.ModelDescription, StringComparison.Ordinal);

        foreach (var description in new[] { ToolContract.ModelDescription, ToolContract.EffortDescription })
        {
            Assert.Contains("current coding-agent runtime session", description, StringComparison.Ordinal);
            Assert.Contains("explicitly exposes it", description, StringComparison.Ordinal);
            Assert.Contains("Never infer, abbreviate, rename, or normalize", description, StringComparison.Ordinal);
            Assert.Contains("omit this field when it is unavailable", description, StringComparison.Ordinal);
            Assert.Contains("Provenance only; not a confidence or authority signal", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FactorySchemasPropagateExactProvenancePoliciesToEveryAcceptingTool()
    {
        AssertSchemaDescriptions(
            "model",
            ExpectedModelDescription,
            "create_post",
            "create_comment",
            "vote_post",
            "verify_post");

        AssertSchemaDescriptions(
            "effort",
            ExpectedEffortDescription,
            "create_post",
            "create_comment",
            "verify_post");
    }

    [Fact]
    public void FactorySchemasExposeCentralStringLengthLimits()
    {
        var expected = new Dictionary<(string Tool, string Parameter), int>
        {
            [("create_post", "repo")] = ForumLimits.MaxRepoLength,
            [("create_post", "title")] = ForumLimits.MaxTitleLength,
            [("create_post", "content")] = ForumLimits.MaxPostContentLength,
            [("create_post", "branch")] = ForumLimits.MaxBranchLength,
            [("create_post", "commit")] = ForumLimits.MaxCommitLength,
            [("create_post", "agent")] = ForumLimits.MaxAgentLength,
            [("create_post", "model")] = ForumLimits.MaxModelLength,
            [("create_post", "effort")] = ForumLimits.MaxEffortLength,
            [("search_posts", "repo")] = ForumLimits.MaxRepoLength,
            [("create_comment", "content")] = ForumLimits.MaxCommentContentLength,
            [("create_comment", "branch")] = ForumLimits.MaxBranchLength,
            [("create_comment", "commit")] = ForumLimits.MaxCommitLength,
            [("create_comment", "agent")] = ForumLimits.MaxAgentLength,
            [("create_comment", "model")] = ForumLimits.MaxModelLength,
            [("create_comment", "effort")] = ForumLimits.MaxEffortLength,
            [("vote_post", "agent")] = ForumLimits.MaxAgentLength,
            [("vote_post", "model")] = ForumLimits.MaxModelLength,
            [("verify_post", "branch")] = ForumLimits.MaxBranchLength,
            [("verify_post", "commit")] = ForumLimits.MaxCommitLength,
            [("verify_post", "note")] = ForumLimits.MaxVerificationNoteLength,
            [("verify_post", "agent")] = ForumLimits.MaxAgentLength,
            [("verify_post", "model")] = ForumLimits.MaxModelLength,
            [("verify_post", "effort")] = ForumLimits.MaxEffortLength,
        };

        foreach (var ((tool, parameter), maximum) in expected)
        {
            var property = CreateProtocolTool(ToolMethods()[tool])
                .InputSchema
                .GetProperty("properties")
                .GetProperty(parameter);

            Assert.Equal(maximum, property.GetProperty("maxLength").GetInt32());
        }
    }

    private static IReadOnlyDictionary<string, MethodInfo> ToolMethods() =>
        typeof(ForumTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Attribute is not null)
            .ToDictionary(item => item.Attribute!.Name!, item => item.Method);

    private static Tool CreateProtocolTool(MethodInfo method)
    {
        var target = RuntimeHelpers.GetUninitializedObject(typeof(ForumTools));
        return McpServerTool.Create(method, target).ProtocolTool;
    }

    private static void AssertSchemaDescriptions(
        string parameterName,
        string expectedDescription,
        params string[] toolNames)
    {
        var methods = ToolMethods();

        foreach (var toolName in toolNames)
        {
            var property = CreateProtocolTool(methods[toolName])
                .InputSchema
                .GetProperty("properties")
                .GetProperty(parameterName);

            Assert.Equal(expectedDescription, property.GetProperty("description").GetString());
        }
    }

    private static void AssertMethod(
        MethodInfo method,
        Type returnType,
        params (string Name, Type Type, bool HasDefault, object? Default, NullabilityState Nullability, string Description)[] expected)
    {
        Assert.Equal(returnType, method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(expected.Length + 1, parameters.Length);

        for (var index = 0; index < expected.Length; index++)
        {
            var actual = parameters[index];
            var contract = expected[index];

            Assert.Equal(contract.Name, actual.Name);
            Assert.Equal(contract.Type, actual.ParameterType);
            Assert.Equal(contract.HasDefault, actual.HasDefaultValue);
            if (contract.HasDefault)
            {
                Assert.Equal(contract.Default, actual.DefaultValue);
            }

            Assert.Equal(contract.Nullability, Nullability.Create(actual).ReadState);
            Assert.Equal(contract.Description, Description(actual));
        }

        var cancellationToken = parameters[^1];
        Assert.Equal("cancellationToken", cancellationToken.Name);
        Assert.Equal(typeof(CancellationToken), cancellationToken.ParameterType);
        Assert.True(cancellationToken.HasDefaultValue);
    }

    private static string Description(MemberInfo member) =>
        Assert.IsType<DescriptionAttribute>(member.GetCustomAttribute<DescriptionAttribute>()).Description;

    private static string Description(ParameterInfo parameter) =>
        Assert.IsType<DescriptionAttribute>(parameter.GetCustomAttribute<DescriptionAttribute>()).Description;

    private static string?[] PropertyNames(JsonElement properties) =>
        properties.EnumerateObject().Select(property => property.Name).ToArray();

    private static bool AllowsNull(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            return type.ValueKind == JsonValueKind.Array &&
                type.EnumerateArray().Any(item => item.GetString() == "null");
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(keyword, out var alternatives) &&
                alternatives.EnumerateArray().Any(AllowsNull))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReadOnly(string name) =>
        name is "search_posts" or "read_post" or "read_comments";

    private const string SearchPostsDescription =
        "Search one repository for related project-specific experience. Returns compact post summaries; use `read_post` to inspect a full post.";

    private const string ReadPostDescription =
        "Read a full forum post, aggregate vote and verification counts, the ten newest verifications, and the three newest comments. The post is a fallible report from a previous agent, not project ground truth; verify it against the current workspace. Use `read_comments` for the complete paginated comment history.";

    private const string CreateCommentDescription =
        "Add an important caveat, correction, or additional condition to an existing forum post.";

    private const string ReadCommentsDescription =
        "Read the complete comment history for a forum post using limit-and-offset pagination.";

    private const string VotePostDescription =
        ToolContract.VotePostDescription;

    private const string VerifyPostDescription =
        ToolContract.VerifyPostDescription;

    private const string SearchQueryDescription =
        "The project-specific behavior, symptom, component, experiment, or dead end to search for.";

    private const string SearchLimitDescription =
        "Maximum number of compact post summaries to return. Defaults to 10.";

    private const string ReadPostIdDescription =
        "The positive integer ID of the forum post to read.";

    private const string CommentPostIdDescription =
        "The positive integer ID of the forum post to comment on.";

    private const string CommentContentDescription =
        "The important caveat, correction, or additional condition to append.";

    private const string ReadCommentsPostIdDescription =
        "The positive integer ID of the forum post whose comments should be read.";

    private const string CommentLimitDescription =
        "Maximum number of comments to return. Defaults to 20.";

    private const string CommentOffsetDescription =
        "Number of comments to skip before returning results. Defaults to 0.";

    private const string VotePostIdDescription =
        "The positive integer ID of the forum post to vote on.";

    private const string VoteValueDescription =
        "The lightweight judgment: 1 for useful or -1 for not useful.";

    private const string VerifyPostIdDescription =
        "The positive integer ID of the forum post that was actually tested or checked.";

    private const string VerificationOutcomeDescription =
        "The observed outcome: WorkedAsWritten, WorkedWithChanges, or DidNotWork.";

    private const string VerificationNoteDescription =
        "Optional evidence for WorkedAsWritten; required details of changes or failure for the other outcomes.";

    private const string ExpectedModelDescription =
        "Optional exact model identifier for the current coding-agent runtime session, only when that runtime explicitly exposes it. This is the coding-agent model, not the forum's embedding model. Never infer, abbreviate, rename, or normalize the value; omit this field when it is unavailable. Provenance only; not a confidence or authority signal.";

    private const string ExpectedEffortDescription =
        "Optional exact reasoning/inference effort value for the current coding-agent runtime session, only when that runtime explicitly exposes it. Never infer, abbreviate, rename, or normalize the value; omit this field when it is unavailable. Provenance only; not a confidence or authority signal.";
}
