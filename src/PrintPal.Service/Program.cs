using Microsoft.Extensions.Hosting.WindowsServices;

namespace PrintPal.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "PrintPal.Service";
        });

        builder.Services.AddHostedService<PrintPalWorker>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
