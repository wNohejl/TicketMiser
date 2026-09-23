using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Tests.Integration;

/// <summary>An <see cref="INotifier"/> that keeps what it was asked to send and sends nothing.</summary>
public sealed class RecordingNotifier : INotifier
{
    private readonly List<MailMessage> _sent = [];

    /// <summary>When set, every send throws this instead of recording.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<MailMessage> Sent
    {
        get
        {
            lock (_sent)
                return _sent.ToList();
        }
    }

    public IReadOnlyList<MailMessage> To(string address) => Sent.Where(m => m.To == address).ToList();

    public Task<string?> SendAsync(MailMessage message, CancellationToken ct)
    {
        if (FailWith is not null)
            throw FailWith;

        lock (_sent)
        {
            _sent.Add(message);
            return Task.FromResult<string?>($"rec-{_sent.Count}");
        }
    }
}
