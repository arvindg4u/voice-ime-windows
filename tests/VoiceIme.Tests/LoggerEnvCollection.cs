using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Process-wide lock for tests that mutate VOICEIME_LOG_LEVEL (LoggerTests).
/// xUnit runs test classes in parallel, and env vars are process-global —
/// the collection definition serializes every class in it. LoggerTests is
/// currently the only member, but the lock also guards against future ones.
/// </summary>
[CollectionDefinition("LoggerEnv")]
public sealed class LoggerEnvCollection : ICollectionFixture<EnvLock>
{
}

/// <summary>Shared lock instance for the LoggerEnv collection.</summary>
public sealed class EnvLock
{
    public static readonly object Instance = new();
}
