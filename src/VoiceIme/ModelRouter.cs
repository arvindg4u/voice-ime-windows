using System.Text.RegularExpressions;

namespace VoiceIme;

/// <summary>Which transport family serves a model. Decided by <see cref="ModelRouter"/>.</summary>
public enum TransportKind
{
    Rest,
    Interactions,
    Live,
}

/// <summary>
/// What a model can do, as far as static analysis can tell. Nullable bools
/// mean Unknown — a name heuristic is a routing candidate, never proof of
/// server capability (official/API behavior stays authoritative).
/// </summary>
public sealed record ModelCapabilities(
    bool? SupportsRest,
    bool? SupportsLive,
    bool? SupportsInteractions,
    bool RequiresLive,
    bool RequiresInteractions);

/// <summary>
/// Centralized model → transport routing (ports Android GeminiTransports
/// routing/fallback classifiers). Normalization is trim + lowercase; the
/// Interactions allowlist is one exact model; Live markers are substring
/// candidates only.
/// </summary>
public static class ModelRouter
{
    public const string InteractionsModel = "gemini-3.5-transcribe";

    private static readonly Regex LiveOnlyError = new(
        @"only supports.*bidiGenerateContent|Please use .*Live API",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DevInstructionError = new(
        @"developer instruction.*not enabled|systemInstruction.*not (supported|enabled)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeModel(string? model) =>
        (model ?? "").Trim().ToLowerInvariant();

    public static bool IsInteractionsModel(string? model) =>
        NormalizeModel(model) == InteractionsModel;

    public static bool IsLiveCandidate(string? model)
    {
        var name = NormalizeModel(model);
        return name.Contains("-live")
            || name.Contains("live-preview")
            || name.Contains("native-audio")
            || name.Contains("-dialog");
    }

    public static TransportKind RouteModel(string? model)
    {
        if (IsInteractionsModel(model)) return TransportKind.Interactions;
        if (IsLiveCandidate(model)) return TransportKind.Live;
        return TransportKind.Rest;
    }

    /// <summary>HTTP 400 whose body proves the model is Live-only. Nothing else qualifies.</summary>
    public static bool IsLiveOnlyError(int httpCode, string? body) =>
        httpCode == 400 && body is not null && LiveOnlyError.IsMatch(body);

    /// <summary>HTTP 400 proving the model rejects developer/system instruction. Nothing else qualifies.</summary>
    public static bool IsDevInstructionError(int httpCode, string? body) =>
        httpCode == 400 && body is not null && DevInstructionError.IsMatch(body);

    public static ModelCapabilities GetCapabilities(string? model)
    {
        if (IsInteractionsModel(model))
            return new(SupportsRest: false, SupportsLive: null, SupportsInteractions: true,
                RequiresLive: false, RequiresInteractions: true);
        if (IsLiveCandidate(model))
            return new(SupportsRest: null, SupportsLive: true, SupportsInteractions: null,
                RequiresLive: false, RequiresInteractions: false);
        return new(SupportsRest: true, SupportsLive: null, SupportsInteractions: null,
            RequiresLive: false, RequiresInteractions: false);
    }
}
