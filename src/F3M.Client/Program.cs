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

// register the cookie handler
builder.Services.AddTransient<CookieHandler>();

// set up authorization
builder.Services.AddAuthorizationCore();

// register the custom state provider
builder.Services.AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>();

// register the account management interface
builder.Services.AddScoped(
    sp => (IAccountManagement)sp.GetRequiredService<AuthenticationStateProvider>());

builder.Services
    .AddHttpClient(Configuration.AppName, client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<CookieHandler>();

// Separate named client for CookieAuthenticationStateProvider (/login, /logout, /manage/info,
// /roles all live at the root, not under /api like everything else).
builder.Services
    .AddHttpClient("Auth", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<CookieHandler>();

// Provide the named client as the default HttpClient
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(Configuration.AppName));

builder.Services.AddScoped<IProfileApi, HttpProfileApi>();
builder.Services.AddScoped<IAdminApi, HttpAdminApi>();
builder.Services.AddScoped<ITelemetryApi, HttpTelemetryApi>();
builder.Services.AddScoped<IF95LinkApi, HttpF95LinkApi>();
builder.Services.AddScoped<IModsApi, HttpModsApi>();

await builder
    .Build()
    .RunAsync();
