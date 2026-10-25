using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 支持批量替换的 <see cref="ObservableCollection{T}"/>。
///
/// 邮件列表一次要给 500 行：逐条 <c>Add</c> 会发 500 次 CollectionChanged，
/// ListBox 每次都要重排一遍（机械盘机器上实测能把首屏拖到一分半），
/// 这里整批替换只发一次 Reset，容器只生成一轮。
/// </summary>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    /// <summary>用一个新集合整体替换现有内容；内容为空时也照样发一次 Reset。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items) Items.Add(item);

        // 一次 Reset：比 500 次 Add 少 499 轮布局与容器生成
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
