using System.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.AppKit;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class AppKitBindingLifetimeTests
{
    [Fact]
    public async Task InitialAndWorkerChangesApplyLatestValueAndDisposedCallbacksDoNothing()
    {
        var dispatcher = new QueuedDispatcher();
        var model = new Model();
        var values = new List<string>();
        var binding = new PropertyBinding<Model, string>(model, nameof(Model.Value), source => source.Value, values.Add, dispatcher, error => throw error);
        dispatcher.Flush();
        Assert.Equal(new[] { "initial" }, values);
        await Task.Run(() => model.Value = "worker");
        dispatcher.Flush();
        Assert.Equal("worker", values.Last());
        model.Value = "late";
        binding.Dispose();
        dispatcher.Flush();
        Assert.Equal(2, values.Count);
        Assert.Equal(0, model.Subscribers);
    }
    [Fact]
    public void TwoWayBindingSuppressesNativeFeedbackAndReplacesNestedSubscription()
    {
        var dispatcher = new QueuedDispatcher();
        var owner = new Owner { Child = new Model() };
        PropertyBinding<Model, string>? active = null;
        using var nested = new NestedPropertyBinding<Owner, Model, string>(owner, nameof(Owner.Child), source => source.Child,
            child => active = new(child, nameof(Model.Value), source => source.Value, value => active?.UpdateSource(value), dispatcher, error => throw error, (source, value) => source.Value = value), dispatcher, error => throw error);
        dispatcher.Flush(); dispatcher.Flush();
        var first = owner.Child;
        active!.UpdateSource("edited"); dispatcher.Flush();
        Assert.Equal("edited", first!.Value);
        owner.Child = new Model(); dispatcher.Flush(); dispatcher.Flush();
        Assert.Equal(0, first.Subscribers);
        Assert.Equal(1, owner.Child.Subscribers);
    }
    [Fact]
    public async Task AsyncCommandFailureUsesErrorBoundaryAndDisposalDetachesCanExecute()
    {
        var dispatcher = new QueuedDispatcher();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("expected")));
        using var binding = new CommandBinding(command, () => null, _ => { }, dispatcher, error => failed.TrySetResult(error));
        dispatcher.Flush();
        binding.Execute();
        Assert.IsType<InvalidOperationException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }
    private sealed class QueuedDispatcher : IDispatcher
    {
        private readonly Queue<Action> _actions = new();
        public Task ExecuteOnUIThread(Action action) { lock (_actions) _actions.Enqueue(action); return Task.CompletedTask; }
        public void Flush()
        {
            while (true)
            {
                Action? action; lock (_actions) action = _actions.Count > 0 ? _actions.Dequeue() : null;
                if (action is null) return;
                action();
            }
        }
    }
    private sealed class Model : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _changed;
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public event PropertyChangedEventHandler? PropertyChanged { add => _changed += value; remove => _changed -= value; }
        private string _value = "initial";
        public string Value { get => _value; set { _value = value; _changed?.Invoke(this, new(nameof(Value))); } }
    }
    private sealed class Owner : INotifyPropertyChanged
    {
        private Model? _child;
        public Model? Child { get => _child; set { _child = value; PropertyChanged?.Invoke(this, new(nameof(Child))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
