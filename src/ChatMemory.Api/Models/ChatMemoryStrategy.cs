namespace ChatMemory.Api.Models;

// How the prompt for the answer model is built from the stored transcript.
public enum ChatMemoryStrategy
{
    // Send the complete transcript. Unsafe baseline, useful only for measurement.
    None,

    // Send the system prompt plus the most recent TargetMessageCount messages.
    Counting,

    // Send the system prompt, a durable summary of older turns, and the recent tail.
    Summarizing,
}
