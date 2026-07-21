namespace AshaLive;

/// <summary>
/// A conversation-only projection of ASHA's timestamped shared-attention
/// timeline. The activity view retains the same event alongside clicks, cues,
/// and future tool actions; this record keeps reading the dialogue calm.
/// </summary>
public sealed record ConversationMessage(DateTime Timestamp, string Speaker, string Text);
