using System.Diagnostics.CodeAnalysis;

namespace Wino.Hosting;

/// <summary>Answers a script queued for the next time a dialog is shown; each answer is used once.</summary>
public sealed class HostDialogAnswers
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<object?>> _answers = new(StringComparer.Ordinal);

    public void Enqueue<T>(string dialogName, T answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogName);

        lock (_gate)
        {
            if (!_answers.TryGetValue(dialogName, out var queue))
                _answers[dialogName] = queue = new Queue<object?>();

            queue.Enqueue(answer);
        }
    }

    internal bool TryTake<T>(string dialogName, [MaybeNullWhen(false)] out T answer)
    {
        lock (_gate)
        {
            if (_answers.TryGetValue(dialogName, out var queue) && queue.Count > 0 && queue.Peek() is T typed)
            {
                queue.Dequeue();
                answer = typed;
                return true;
            }
        }

        answer = default;
        return false;
    }
}
