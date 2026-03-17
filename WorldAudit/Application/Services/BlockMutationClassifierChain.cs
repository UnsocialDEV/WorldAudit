using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Application.Services;

public sealed class BlockMutationClassifierChain : IBlockMutationClassifier
{
    private static readonly string[] GravityTokens = ["gravel", "sand", "loose", "falling"];
    private static readonly string[] DecayTokens = ["decay", "decayed", "rotten", "rotted", "dead", "ruined"];

    private readonly WorldAuditOptions _options;

    public BlockMutationClassifierChain(WorldAuditOptions options)
    {
        _options = options;
    }

    public BlockMutationClassification Classify(BlockMutationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (TryClassifyPlayer(observation, out var classification))
        {
            return classification;
        }

        if (TryClassifyExplicitSystem(observation, out classification))
        {
            return classification;
        }

        if (TryClassifyNatural(observation, out classification))
        {
            return classification;
        }

        return BlockMutationClassification.Synthetic(AuditCause.Unknown);
    }

    private bool TryClassifyPlayer(BlockMutationObservation observation, out BlockMutationClassification classification)
    {
        classification = default!;
        return TryScopeClassification(observation.PositionScope, AuditCause.Player, out classification)
            || TryScopeClassification(observation.AmbientScope, AuditCause.Player, out classification);
    }

    private bool TryClassifyExplicitSystem(BlockMutationObservation observation, out BlockMutationClassification classification)
    {
        classification = default!;

        if ((_options.EnableExplosionCauseProvider &&
                (TryScopeClassification(observation.PositionScope, AuditCause.Explosion, out classification) ||
                 TryScopeClassification(observation.AmbientScope, AuditCause.Explosion, out classification))) ||
            (_options.EnableSystemCauseProvider &&
                (TryScopeClassification(observation.PositionScope, AuditCause.System, out classification) ||
                 TryScopeClassification(observation.AmbientScope, AuditCause.System, out classification))) ||
            (_options.EnableWorldgenCauseProvider &&
                (TryScopeClassification(observation.PositionScope, AuditCause.Worldgen, out classification) ||
                 TryScopeClassification(observation.AmbientScope, AuditCause.Worldgen, out classification))))
        {
            return true;
        }

        return false;
    }

    private bool TryClassifyNatural(BlockMutationObservation observation, out BlockMutationClassification classification)
    {
        classification = default!;
        if (!_options.EnableNaturalCauseAudit)
        {
            return false;
        }

        if (_options.EnableFireCauseProvider &&
            (TryScopeClassification(observation.PositionScope, AuditCause.Fire, out classification) ||
             TryScopeClassification(observation.AmbientScope, AuditCause.Fire, out classification)))
        {
            return true;
        }

        if (_options.EnableFireCauseProvider && MatchesFireContext(observation))
        {
            classification = BlockMutationClassification.Synthetic(AuditCause.Fire);
            return true;
        }

        if (_options.EnableGravityCauseProvider &&
            (TryScopeClassification(observation.PositionScope, AuditCause.Gravity, out classification) ||
             TryScopeClassification(observation.AmbientScope, AuditCause.Gravity, out classification)))
        {
            return true;
        }

        if (_options.EnableGravityCauseProvider && MatchesAnyToken(observation, GravityTokens))
        {
            classification = BlockMutationClassification.Synthetic(AuditCause.Gravity);
            return true;
        }

        if (_options.EnableDecayCauseProvider &&
            (TryScopeClassification(observation.PositionScope, AuditCause.Decay, out classification) ||
             TryScopeClassification(observation.AmbientScope, AuditCause.Decay, out classification)))
        {
            return true;
        }

        if (_options.EnableDecayCauseProvider && MatchesAnyToken(observation, DecayTokens))
        {
            classification = BlockMutationClassification.Synthetic(AuditCause.Decay);
            return true;
        }

        return false;
    }

    private static bool TryScopeClassification(
        BlockMutationScope? scope,
        AuditCause expectedCause,
        out BlockMutationClassification classification)
    {
        classification = default!;
        if (scope is null || !scope.IsTrusted || scope.Cause != expectedCause)
        {
            return false;
        }

        classification = new BlockMutationClassification(
            scope.ActorName,
            scope.ActorExternalId,
            scope.Cause,
            scope.IsSynthetic);

        return true;
    }

    private static bool MatchesAnyToken(BlockMutationObservation observation, IReadOnlyList<string> tokens)
    {
        if (ContainsAnyToken(observation.OldBlockCode, tokens))
        {
            return true;
        }

        if (ContainsAnyToken(observation.NewBlockCode, tokens))
        {
            return true;
        }

        return ContainsAnyToken(observation.CurrentBlockCode, tokens);
    }

    private static bool MatchesFireContext(BlockMutationObservation observation)
    {
        return observation.HasFireNeighbor
            || ContainsToken(observation.OldBlockCode, "fire")
            || ContainsToken(observation.NewBlockCode, "fire")
            || ContainsToken(observation.CurrentBlockCode, "fire");
    }

    private static bool ContainsAnyToken(string? candidate, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (candidate.Contains(tokens[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsToken(string? candidate, string token)
    {
        return !string.IsNullOrWhiteSpace(candidate)
            && candidate.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
