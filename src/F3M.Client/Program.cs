using F3M.Client;
using F3M.Client.Identity;
using F3M.Shared;
using F3M.Shared.Api;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register the cookie handler for API and account-management requests.
builder.Services.AddTransient<CookieHandler>();

builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>();
builder.Services.AddScoped(
    sp => (IAccountManagement)sp.GetRequiredService<AuthenticationStateProvider>());

builder.Services
    .AddHttpClient(Configuration.AppName, client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<CookieHandler>();

// Account-management endpoints are rooted outside /api, unlike the generated API clients.
builder.Services
    .AddHttpClient("Auth", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<CookieHandler>();

builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(Configuration.AppName));

builder.Services.AddScoped<IProfileApi, HttpProfileApi>();
builder.Services.AddScoped<IAdminApi, HttpAdminApi>();
builder.Services.AddScoped<ITelemetryApi, HttpTelemetryApi>();
builder.Services.AddScoped<IF95LinkApi, HttpF95LinkApi>();
builder.Services.AddScoped<IModsApi, HttpModsApi>();

await builder
    .Build()
    .RunAsync();
