using F3M.Client;
using F3M.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Auth state
builder.Services.AddSingleton<F3MAuthStateProvider>();
builder.Services.AddSingleton<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<F3MAuthStateProvider>());
builder.Services.AddAuthorizationCore();

// HTTP with auth handler
builder.Services.AddSingleton<AuthHttpHandler>();
builder.Services.AddHttpClient("F3M", client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<AuthHttpHandler>();

// Provide the named client as the default HttpClient
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("F3M"));

builder.Services.AddScoped<AuthService>();

await builder.Build().RunAsync();
