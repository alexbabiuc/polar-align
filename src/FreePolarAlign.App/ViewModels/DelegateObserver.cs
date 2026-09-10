namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// A minimal <see cref="IObserver{T}"/> adapter so <see cref="FreePolarAlign.Core.Engine.IAlignmentEngine.Events"/>
/// (a plain BCL <see cref="IObservable{T}"/>, chosen per its own doc comment so
/// callers do not need a reactive-extensions dependency) can be subscribed with
/// a single delegate.
/// </summary>
internal sealed class DelegateObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    private readonly Action<Exception>? _onError;

    public DelegateObserver(Action<T> onNext, Action<Exception>? onError = null)
    {
        _onNext = onNext ?? throw new ArgumentNullException(nameof(onNext));
        _onError = onError;
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error) => _onError?.Invoke(error);

    public void OnNext(T value) => _onNext(value);
}
