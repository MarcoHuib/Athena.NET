using System.Security.Cryptography;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default per-connection <see cref="IServiceAuthenticationService"/>: HMAC-SHA256
/// challenge/response over the configured CharServer ServiceToken. A fresh
/// instance is created per TCP connection (see ServiceComposition), so the
/// one-outstanding-challenge state and IsAuthenticated flag are naturally
/// scoped to a single connection - one connection's issued nonce/proof can
/// never authenticate a different connection.
/// </summary>
public sealed class ServiceAuthenticationService : IServiceAuthenticationService
{
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(30);

    private readonly CharServerServiceTokenProvider _tokenProvider;
    private PendingChallenge? _pendingChallenge;

    public ServiceAuthenticationService(CharServerServiceTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public bool IsAuthenticated { get; private set; }

    public string? ServiceId { get; private set; }

    public byte[] GenerateChallenge(string serviceId)
    {
        ServiceId = serviceId;
        var nonce = RandomNumberGenerator.GetBytes(PacketConstants.ServiceNonceLength);
        _pendingChallenge = new PendingChallenge(serviceId, nonce, DateTime.UtcNow);
        return nonce;
    }

    public ServiceAuthenticationResult VerifyProof(byte[] proof)
    {
        // Consumed unconditionally, before any check below runs: a challenge
        // can be satisfied at most once, whether the proof that consumes it is
        // right or wrong.
        var challenge = _pendingChallenge;
        _pendingChallenge = null;

        if (!_tokenProvider.IsConfigured)
        {
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.TokenNotConfigured);
        }

        if (challenge == null)
        {
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.NoChallengeIssued);
        }

        if (DateTime.UtcNow - challenge.IssuedAt > ChallengeTimeout)
        {
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.ChallengeExpired);
        }

        var expected = ServiceAuthProofCalculator.ComputeProof(_tokenProvider.TokenBytes!, challenge.ServiceId, challenge.Nonce);

        if (expected.Length != proof.Length || !CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.InvalidProof);
        }

        return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.Success);
    }

    public void MarkAuthenticated()
    {
        IsAuthenticated = true;
    }

    private sealed record PendingChallenge(string ServiceId, byte[] Nonce, DateTime IssuedAt);
}
