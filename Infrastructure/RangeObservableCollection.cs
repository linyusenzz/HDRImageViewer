using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HdrImageViewer.Infrastructure;

/// <summary>
/// ObservableCollection with a batch replace that raises a single Reset
/// notification. Replacing thousands of items one Add at a time makes a bound
/// ListView process one collection-change per item on the UI thread, which
/// freezes the app for large folders.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> newItems)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in newItems)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
