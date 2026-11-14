using Tatehama.Shukkindai.Server.Data;

namespace Tatehama.Shukkindai.Server.Repositories.Database;

public sealed class DatabaseRepository(ApplicationDbContext context) : IDatabaseRepository
{
    public Task<bool> CanConnect(CancellationToken cancellationToken) =>
        context.Database.CanConnectAsync(cancellationToken);
}
