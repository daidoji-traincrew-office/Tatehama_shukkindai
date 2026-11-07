using Tatehama.Shukkindai.Server.IT.Fixture;

namespace Tatehama.Shukkindai.Server.IT.Collection;

[CollectionDefinition(Name)]
public sealed class WebApplicationCollection : ICollectionFixture<WebApplicationFixture>
{
    public const string Name = "WebApplication";
}
