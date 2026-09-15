using Xunit;

namespace VoiceIme.Tests;

public sealed class ModelRouterTests
{
    [Theory]
    [InlineData("gemini-2.5-flash", TransportKind.Rest)]
    [InlineData("gemini-2.0-flash", TransportKind.Rest)]
    [InlineData("gemini-1.5-pro", TransportKind.Rest)]
    [InlineData("transcribe", TransportKind.Rest)]
    [InlineData("my-transcribe-helper", TransportKind.Rest)]
    [InlineData("", TransportKind.Rest)]
    [InlineData("gemini-3.5-transcribe", TransportKind.Interactions)]
    [InlineData("Gemini-3.5-Transcribe", TransportKind.Interactions)]
    [InlineData("  gemini-3.5-transcribe  ", TransportKind.Interactions)]
    [InlineData("gemini-3.5-transcribe-extra", TransportKind.Rest)]
    [InlineData("gemini-3.5-transcribe-v2", TransportKind.Rest)]
    [InlineData("gemini-live-2.5", TransportKind.Live)]
    [InlineData("gemini-2.5-flash-live", TransportKind.Live)]
    [InlineData("GEMINI-2.5-FLASH-LIVE", TransportKind.Live)]
    [InlineData("live-preview-foo", TransportKind.Live)]
    [InlineData("native-audio-1", TransportKind.Live)]
    [InlineData("my-dialog-model", TransportKind.Live)]
    public void RouteModel_Matrix_MatchesContract(string model, TransportKind expected)
    {
        Assert.Equal(expected, ModelRouter.RouteModel(model));
    }

    [Fact]
    public void RouteModel_NullModel_RoutesRest()
    {
        Assert.Equal(TransportKind.Rest, ModelRouter.RouteModel(null));
    }

    [Fact]
    public void NormalizeModel_TrimsAndLowercases()
    {
        Assert.Equal("gemini-3.5-transcribe", ModelRouter.NormalizeModel("  Gemini-3.5-Transcribe\t"));
    }

    [Fact]
    public void GetCapabilities_TranscribeModel_RequiresInteractions()
    {
        var caps = ModelRouter.GetCapabilities("gemini-3.5-transcribe");

        Assert.True(caps.RequiresInteractions);
        Assert.True(caps.SupportsInteractions);
        Assert.False(caps.RequiresLive);
        Assert.False(caps.SupportsRest);
    }

    [Fact]
    public void GetCapabilities_LiveCandidate_MarksSupportWithoutRequiring()
    {
        var caps = ModelRouter.GetCapabilities("gemini-2.5-flash-live");

        Assert.True(caps.SupportsLive);
        Assert.False(caps.RequiresLive);
        Assert.Null(caps.SupportsRest);
    }

    [Fact]
    public void GetCapabilities_OrdinaryModel_SupportsRestOnly()
    {
        var caps = ModelRouter.GetCapabilities("gemini-2.5-flash");

        Assert.True(caps.SupportsRest);
        Assert.False(caps.RequiresLive);
        Assert.False(caps.RequiresInteractions);
        Assert.Null(caps.SupportsLive);
    }

    [Theory]
    [InlineData(400, "This model only supports bidiGenerateContent, migrate now", true, false)]
    [InlineData(400, "Please use the Live API for this model", true, false)]
    [InlineData(400, "ONLY SUPPORTS BIDIGENERATECONTENT", true, false)]
    [InlineData(400, "Developer instruction is not enabled for this model", false, true)]
    [InlineData(400, "systemInstruction is not supported by this model", false, true)]
    [InlineData(400, "systemInstruction is not enabled", false, true)]
    [InlineData(400, "something else failed badly", false, false)]
    [InlineData(400, "", false, false)]
    [InlineData(401, "Please use the Live API for this model", false, false)]
    [InlineData(429, "only supports bidiGenerateContent", false, false)]
    [InlineData(500, "systemInstruction is not supported", false, false)]
    [InlineData(404, "Developer instruction is not enabled", false, false)]
    public void ErrorClassifiers_OnlyNarrow400PatternsMatch(int code, string body, bool live, bool dev)
    {
        Assert.Equal(live, ModelRouter.IsLiveOnlyError(code, body));
        Assert.Equal(dev, ModelRouter.IsDevInstructionError(code, body));
    }
}
