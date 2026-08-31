using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

[assembly: HostingStartup(typeof(ImportMapExtensionStartup))]

namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

public class ImportMapExtensionStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, ImportMapExtensionStartupFilter>();
        });
    }
}
