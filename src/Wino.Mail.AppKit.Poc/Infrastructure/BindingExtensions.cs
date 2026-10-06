using AppKit;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Windows.Input;

namespace Wino.Mail.AppKit.Poc.Infrastructure;

internal static class BindingExtensions
{
    public static IDisposable BindText<TViewModel>(
        this NSTextField control,
        TViewModel viewModel,
        Expression<Func<TViewModel, string>> property)
        where TViewModel : INotifyPropertyChanged
    {
        var propertyName = GetPropertyName(property);
        var getter = property.Compile();
        var disposed = false;

        void Update() => NSApplication.SharedApplication.InvokeOnMainThread(
            () =>
            {
                if (!disposed)
                    control.StringValue = getter(viewModel) ?? string.Empty;
            });

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == propertyName)
                Update();
        };

        viewModel.PropertyChanged += handler;
        Update();
        return new ActionDisposable(() =>
        {
            disposed = true;
            viewModel.PropertyChanged -= handler;
        });
    }

    public static IDisposable BindCommand(this NSButton button, ICommand command)
    {
        void UpdateCanExecute() => NSApplication.SharedApplication.InvokeOnMainThread(
            () =>
            {
                if (!disposed)
                    button.Enabled = command.CanExecute(null);
            });

        var disposed = false;
        EventHandler canExecuteChanged = (_, _) => UpdateCanExecute();
        EventHandler activated = (_, _) =>
        {
            if (!disposed && command.CanExecute(null))
                command.Execute(null);
        };

        command.CanExecuteChanged += canExecuteChanged;
        button.Activated += activated;
        UpdateCanExecute();

        return new ActionDisposable(() =>
        {
            disposed = true;
            command.CanExecuteChanged -= canExecuteChanged;
            button.Activated -= activated;
        });
    }

    private static string GetPropertyName<TViewModel, TValue>(Expression<Func<TViewModel, TValue>> property)
    {
        Expression body = property.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } conversion)
            body = conversion.Operand;

        return body is MemberExpression { Member: { } member }
            ? member.Name
            : throw new ArgumentException("Binding expressions must select a property directly.", nameof(property));
    }
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
