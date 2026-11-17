#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wino.Core.Domain.ComponentModel;

/// <summary>
/// Change notification for the observable types this project keeps, without a toolkit reference.
/// It mirrors the members and the event order of CommunityToolkit.Mvvm's <c>ObservableObject</c>,
/// which these types used before, so subscribers see the same notifications.
/// </summary>
/// <remarks>
/// A type that derives from this class cannot use the toolkit's <c>[ObservableProperty]</c>, because
/// the generator only accepts <c>ObservableObject</c> bases. Write the property by hand: compare with
/// <see cref="EqualityComparer{T}.Default"/>, raise changing, assign, run the change hook, raise changed,
/// then raise the dependent properties.
/// </remarks>
public abstract class ObservableModel : INotifyPropertyChanged, INotifyPropertyChanging
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public event PropertyChangingEventHandler? PropertyChanging;

    protected virtual void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        PropertyChanged?.Invoke(this, e);
    }

    protected virtual void OnPropertyChanging(PropertyChangingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        PropertyChanging?.Invoke(this, e);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

    protected void OnPropertyChanging([CallerMemberName] string? propertyName = null)
        => OnPropertyChanging(new PropertyChangingEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, newValue))
            return false;

        OnPropertyChanging(propertyName);

        field = newValue;

        OnPropertyChanged(propertyName);

        return true;
    }

    protected bool SetProperty<TModel, T>(T oldValue, T newValue, TModel model, Action<TModel, T> callback, [CallerMemberName] string? propertyName = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(callback);

        if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
            return false;

        OnPropertyChanging(propertyName);

        callback(model, newValue);

        OnPropertyChanged(propertyName);

        return true;
    }
}
