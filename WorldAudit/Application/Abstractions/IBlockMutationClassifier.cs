using WorldAudit.Integration;

namespace WorldAudit.Application.Abstractions;

public interface IBlockMutationClassifier
{
    BlockMutationClassification Classify(BlockMutationObservation observation);
}
