using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Mathom.Tests;

[Collection("postgres")]
public class VoiceCaptureControllerTests
{
    private readonly PostgresFixture _fx;
    public VoiceCaptureControllerTests(PostgresFixture fx) => _fx = fx;

    private async Task<TestWebAppFactory> CreateAppAsync(FakeMediaStore media)
    {
        var app = new TestWebAppFactory(_fx.ConnectionString, s =>
        {
            s.RemoveAll(typeof(IMediaStore));
            s.AddSingleton<IMediaStore>(media);
        });
        await app.SeedUsersAsync();
        return app;
    }

    private static MultipartFormDataContent VoicePayload(byte[] audio, string idempotencyKey)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
        form.Add(file, "audio", "note.webm");
        form.Add(new StringContent(idempotencyKey), "idempotencyKey");
        return form;
    }

    [Fact]
    public async Task Post_Voice_CreatesPendingVoiceItem_WithMediaPath()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        var resp = await client.PostAsync("/capture/voice", VoicePayload(new byte[] { 1, 2, 3 }, "voice-1"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = _fx.NewDbContext();
        var item = await db.Items.SingleAsync(i => i.IdempotencyKey == "voice-1");
        Assert.Equal(SourceType.Voice, item.SourceType);
        Assert.Equal(ItemStatus.Pending, item.Status);
        Assert.False(string.IsNullOrEmpty(item.MediaPath));
        Assert.Equal("", item.RawText);
    }

    [Fact]
    public async Task Post_Voice_IsIdempotent()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        await client.PostAsync("/capture/voice", VoicePayload(new byte[] { 1 }, "voice-dup"));
        var second = await client.PostAsync("/capture/voice", VoicePayload(new byte[] { 1 }, "voice-dup"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var db = _fx.NewDbContext();
        Assert.Equal(1, await db.Items.CountAsync(i => i.IdempotencyKey == "voice-dup"));
    }

    [Fact]
    public async Task Post_Voice_RejectsMissingAudio()
    {
        var media = new FakeMediaStore();
        using var app = await CreateAppAsync(media);
        var client = app.CreateClient();

        var form = new MultipartFormDataContent { { new StringContent("voice-empty"), "idempotencyKey" } };
        var resp = await client.PostAsync("/capture/voice", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
