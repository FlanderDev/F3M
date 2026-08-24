using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using FlanderDev.RouteGen;
using System.Net;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Client.Pages.Managment;

public partial class PublicProfile
{
    [Parameter]
    public string Username { get; set; } = string.Empty;

    private PublicProfileDto? profile;
    private bool loading = true;
    private bool notFound;
    private string? loadError;

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        loading = true;
        notFound = false;
        loadError = null;
        profile = null;

        try
        {
            profile = await Api.GetPublicProfileAsync(Username);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            notFound = true;
        }
        catch (Exception)
        {
            loadError = "Failed to load this profile. Please try again.";
        }
        finally
        {
            loading = false;
        }
    }
}
