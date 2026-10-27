using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DemoApi.Tests;

public sealed class StartupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("<API_APP_ID>")]
    public void Refuses_to_start_without_an_audience(string audience)
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Frontegg:Audience", audience));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Frontegg:Audience", ex.ToString());
    }
}
