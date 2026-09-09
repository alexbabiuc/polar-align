using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.Session;

/// <summary>
/// A minimal multicast observable for the engine's event stream.
///
/// Hand-written rather than taken from a reactive library because the contract
/// deliberately exposes plain <see cref="IObservable{T}"/> (D6) and this is all
/// it needs: subscribe, publish, unsubscribe. Pulling in a dependency for
/// thirty lines would put a third-party type on a frozen interface.
/// </summary>
internal sealed class EventStream : IObservable<EngineEvent>, IDisposable
{
    private readonly List<IObserver<EngineEvent>> _observers = new();
    private readonly object _gate = new();
    private bool _completed;

    public IDisposable Subscribe(IObserver<EngineEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            if (_completed)
            {
                observer.OnCompleted();
                return new Subscription(this, null);
            }

            _observers.Add(observer);
            return new Subscription(this, observer);
        }
    }

    public void Publish(EngineEvent engineEvent)
    {
        IObserver<EngineEvent>[] snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            snapshot = _observers.ToArray();
        }

        foreach (IObserver<EngineEvent> observer in snapshot)
        {
            // One observer throwing must not stop the others from being told,
            // nor take down the sequence that is publishing.
            try
            {
                observer.OnNext(engineEvent);
            }
            catch (Exception)
            {
            }
        }
    }

    public void Dispose()
    {
        IObserver<EngineEvent>[] snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            snapshot = _observers.ToArray();
            _observers.Clear();
        }

        foreach (IObserver<EngineEvent> observer in snapshot)
        {
            try
            {
                observer.OnCompleted();
            }
            catch (Exception)
            {
            }
        }
    }

    private void Remove(IObserver<EngineEvent> observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EventStream _stream;
        private IObserver<EngineEvent>? _observer;

        public Subscription(EventStream stream, IObserver<EngineEvent>? observer)
        {
            _stream = stream;
            _observer = observer;
        }

        public void Dispose()
        {
            if (_observer is not null)
            {
                _stream.Remove(_observer);
                _observer = null;
            }
        }
    }
}
