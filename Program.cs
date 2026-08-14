using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

var webpageRoot = Environment.GetEnvironmentVariable("WebpageRoot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = string.IsNullOrWhiteSpace(webpageRoot) ? null : webpageRoot
});

var credentialsDirectory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
if (!string.IsNullOrWhiteSpace(credentialsDirectory))
    builder.Configuration.AddKeyPerFile(credentialsDirectory, optional: true);

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);

builder.Services.AddHttpClient("discord", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient("cats", client =>
{
    client.BaseAddress = new Uri("https://cataas.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddOptions<WebhookOptions>()
    .BindConfiguration("Webhooks")
    .Validate(options =>
        Uri.TryCreate(options.DiscordUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase),
        "Webhooks:DiscordUrl must be a valid HTTPS discord.com webhook URL.")
    .ValidateOnStart();

var stateDirectory = Environment.GetEnvironmentVariable("STATE_DIRECTORY")
    ?? Path.Combine(AppContext.BaseDirectory, "state");
builder.Services.AddSingleton(new PersistentVisitorCounter(
    Path.Combine(stateDirectory, "visitor-count.txt")));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("drawings", _ => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: "anonymous-drawings",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();

app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost("/api/visitor-count", async (
    PersistentVisitorCounter counter,
    CancellationToken cancellationToken) =>
{
    var count = await counter.IncrementAsync(cancellationToken);
    return Results.Ok(new { count });
});

app.MapGet("/api/cat", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    const int maxCatBytes = 5_000_000;

    try
    {
        using var response = await httpClientFactory
            .CreateClient("cats")
            .GetAsync("cat", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return Results.Problem("The cats are napping. Please try again.", statusCode: 502);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Results.Problem("The cat service returned something unexpected.", statusCode: 502);

        if (response.Content.Headers.ContentLength > maxCatBytes)
            return Results.Problem("That cat picture is too large.", statusCode: 502);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > maxCatBytes)
            return Results.Problem("That cat picture is too large.", statusCode: 502);

        context.Response.Headers.CacheControl = "no-store";
        return Results.File(bytes, contentType);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("The cats are napping. Please try again.", statusCode: 502);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("The cat picture took too long to arrive.", statusCode: 504);
    }
});

app.MapPost("/api/drawings", async (
    HttpRequest request,
    IOptions<WebhookOptions> webhookOptions,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    const long maxDrawingBytes = 1_000_000;

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected a drawing upload." });

    var form = await request.ReadFormAsync(cancellationToken);
    var drawing = form.Files.GetFile("drawing");
    var message = form["message"].ToString().Trim();

    if (message.Length > 1000)
        return Results.BadRequest(new { error = "Your message must be 1,000 characters or less." });

    if (drawing is null || drawing.Length == 0)
        return Results.BadRequest(new { error = "Your drawing is empty." });

    if (drawing.Length > maxDrawingBytes)
        return Results.BadRequest(new { error = "Your drawing is too large." });

    await using var drawingStream = new MemoryStream((int)drawing.Length);
    await drawing.CopyToAsync(drawingStream, cancellationToken);
    var drawingBytes = drawingStream.ToArray();

    ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    if (drawingBytes.Length < pngSignature.Length ||
        !drawingBytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
    {
        return Results.BadRequest(new { error = "Only PNG drawings are accepted." });
    }

    using var multipart = new MultipartFormDataContent();
    var content = "**🎨 New anonymous drawing for Charlotte!**";
    if (!string.IsNullOrWhiteSpace(message))
    {
        var safeMessage = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("```", "``\u200B`", StringComparison.Ordinal);

        content += $"\n\n**💌 Attached message:**\n```text\n{safeMessage}\n```";
    }
    else
    {
        content += "\n\n**💌 Attached message:** *(none)*";
    }

    var payload = JsonSerializer.Serialize(new
    {
        content,
        allowed_mentions = new { parse = Array.Empty<string>() }
    });
    multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

    var imageContent = new ByteArrayContent(drawingBytes);
    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    multipart.Add(imageContent, "files[0]", "anonymous-drawing.png");

    using var response = await httpClientFactory
        .CreateClient("discord")
        .PostAsync(webhookOptions.Value.DiscordUrl, multipart, cancellationToken);

    if (!response.IsSuccessStatusCode)
        return Results.Problem("Discord did not accept the drawing. Please try again.", statusCode: 502);

    return Results.Ok(new { message = "Drawing sent!" });
})
.DisableAntiforgery()
.RequireRateLimiting("drawings");

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.MapFallbackToFile("index.html");

app.Run();

sealed class WebhookOptions
{
    public string DiscordUrl { get; init; } = "";
}

sealed class PersistentVisitorCounter
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _count;

    public PersistentVisitorCounter(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && long.TryParse(File.ReadAllText(path), out var savedCount))
            _count = Math.Max(0, savedCount);
    }

    public async Task<long> IncrementAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _count = checked(_count + 1);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, _count.ToString(), cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
            return _count;
        }
        finally
        {
            _gate.Release();
        }
    }
}
