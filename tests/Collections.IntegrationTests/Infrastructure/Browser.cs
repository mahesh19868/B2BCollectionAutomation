using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Collections.IntegrationTests.Infrastructure;

/// <summary>A signed-in test user driving the app through real HTTP requests, forms and antiforgery tokens.</summary>
public sealed partial class Browser
{
    private Browser(AppFactory factory, HttpClient client, string email, int businessId)
    {
        Factory = factory;
        Client = client;
        Email = email;
        BusinessId = businessId;
    }

    public AppFactory Factory { get; }
    public HttpClient Client { get; }
    public string Email { get; }
    public int BusinessId { get; }

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    public static async Task<Browser> RegisterAsync(AppFactory factory, string? businessName = null)
    {
        var client = factory.CreateBrowser();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var response = await PostFormAsync(client, "/Account/Register", "/Account/Register", new()
        {
            ["BusinessName"] = businessName ?? "Test Distributors " + Guid.NewGuid().ToString("N")[..6],
            ["Email"] = email,
            ["Password"] = "Sup3rSecretPass",
            ["ConfirmPassword"] = "Sup3rSecretPass",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await using var db = factory.CreateDbContext(null);
        var businessId = await db.Users.Where(u => u.Email == email).Select(u => u.BusinessId!.Value).SingleAsync();
        return new Browser(factory, client, email, businessId);
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Client.GetAsync(url);

    public async Task<string> GetHtmlAsync(string url)
    {
        var response = await Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Loads <paramref name="formPageUrl"/> to obtain an antiforgery token, then posts the form.</summary>
    public Task<HttpResponseMessage> PostFormAsync(string formPageUrl, string postUrl, Dictionary<string, string> fields)
        => PostFormAsync(Client, formPageUrl, postUrl, fields);

    public async Task<HttpResponseMessage> UploadAsync(string fileName, string csv)
    {
        var token = await GetTokenAsync(Client, "/Import");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(token), "__RequestVerificationToken");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", fileName);
        return await Client.PostAsync("/Import/Upload", content);
    }

    public static int IdFromLocation(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        return int.Parse(location[(location.LastIndexOf('/') + 1)..].Split('?')[0]);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formPageUrl, string postUrl, Dictionary<string, string> fields)
    {
        var token = await GetTokenAsync(client, formPageUrl);
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(body));
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} returned {(int)response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync();
        var match = TokenRegex().Match(html);
        Assert.True(match.Success, $"No antiforgery token on {url}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
