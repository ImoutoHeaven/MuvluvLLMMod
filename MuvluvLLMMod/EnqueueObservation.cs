namespace MuvluvLLMMod;

public readonly record struct EnqueueObservation(
    bool DurablyPending,
    bool AcceptedByLiveWorker);
