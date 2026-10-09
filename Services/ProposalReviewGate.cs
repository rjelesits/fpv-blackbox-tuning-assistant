namespace FPVBlackboxTuningAssistant.Services;

/// <summary>
/// Tracks the user's review of both the explanation and exact CLI text before export.
/// </summary>
public sealed class ProposalReviewGate
{
    public bool HasViewedChangeDetails { get; private set; }
    public bool HasViewedCliDraft { get; private set; }
    public bool UserConfirmed { get; private set; }

    public bool CanConfirm => HasViewedChangeDetails && HasViewedCliDraft;
    public bool CanExport => CanConfirm && UserConfirmed;

    public void MarkChangeDetailsViewed() => HasViewedChangeDetails = true;

    public void MarkCliDraftViewed() => HasViewedCliDraft = true;

    public void SetUserConfirmed(bool confirmed) => UserConfirmed = confirmed && CanConfirm;
}
