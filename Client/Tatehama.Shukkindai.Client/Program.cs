using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tatehama.Shukkindai.Client;
using Tatehama.Shukkindai.Client.Core;
using Tatehama.Shukkindai.TokenFile;

// 同じ refresh token を 2 プロセスが使うと連鎖失効するので、二重起動を防ぐ
using var mutex = new Mutex(true, @"Local\Tatehama.Shukkindai.Client", out var createdNew);
if (!createdNew)
{
    MessageBox.Show("出勤台クライアントは既に起動しています。", "出勤台", MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

ApplicationConfiguration.Initialize();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
// 接続先(Shukkindai:Issuer)は、リポジトリに置かない appsettings.Local.json で渡す
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
builder.Services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
builder.Services.AddShukkindaiClient(builder.Configuration);
builder.Services.AddSingleton<MainForm>();

// OpenIddict の SystemIntegration が使うホストを動かしたまま、フォームを回す
using var host = builder.Build();
host.Start();
try
{
    using var form = host.Services.GetRequiredService<MainForm>();
    Application.Run(form);
}
finally
{
    host.StopAsync().GetAwaiter().GetResult();
}
