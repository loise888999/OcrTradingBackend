using OcrTradingBackend.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace OcrTradingBackend.Services;

public sealed record CoordinateStreamEvent(
    int? Id,
    int X,
    int Y,
    string RawText,
    DateTime CapturedAtUtc);

public interface ICoordinateStreamService
{
    CoordinateStreamSubscription Subscribe();
    void Publish(ParsedCoordinate coordinate);
    void Publish(CoordinateCapture coordinate);
    void Unsubscribe(Guid subscriptionId);
}

public sealed record CoordinateStreamSubscription(
    Guid Id,
    ChannelReader<CoordinateStreamEvent> Reader);

public sealed class CoordinateStreamService : ICoordinateStreamService
{
    private readonly ConcurrentDictionary<Guid, Channel<CoordinateStreamEvent>> _subscribers = new();

    public CoordinateStreamSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<CoordinateStreamEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        _subscribers[id] = channel;
        return new CoordinateStreamSubscription(id, channel.Reader);
    }

    public void Publish(ParsedCoordinate coordinate)
    {
        Publish(new CoordinateStreamEvent(
            Id: null,
            X: coordinate.X,
            Y: coordinate.Y,
            RawText: coordinate.RawText,
            CapturedAtUtc: DateTime.UtcNow));
    }

    public void Publish(CoordinateCapture coordinate)
    {
        Publish(new CoordinateStreamEvent(
            Id: coordinate.Id == 0 ? null : coordinate.Id,
            X: coordinate.X,
            Y: coordinate.Y,
            RawText: coordinate.RawText,
            CapturedAtUtc: coordinate.CapturedAtUtc));
    }

    public void Unsubscribe(Guid subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var channel))
            channel.Writer.TryComplete();
    }

    private void Publish(CoordinateStreamEvent coordinate)
    {
        foreach (var subscriber in _subscribers.ToArray())
        {
            if (!subscriber.Value.Writer.TryWrite(coordinate))
            {
                if (_subscribers.TryRemove(subscriber.Key, out var channel))
                    channel.Writer.TryComplete();
            }
        }
    }
}