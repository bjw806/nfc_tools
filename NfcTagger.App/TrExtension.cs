using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Markup;
using NfcTagger.Core;

namespace NfcTagger.App;

// {local:Tr Name}: the AppStrings property Name, updated when the language changes.
public sealed class TrExtension(string name) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedText.Value)) { Source = new LocalizedText(name), Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

public sealed class LocalizedText : INotifyPropertyChanged
{
    private readonly PropertyInfo _property;

    public LocalizedText(string name)
    {
        _property = typeof(AppStrings).GetProperty(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ArgumentException($"AppStrings has no {name}.", nameof(name));
        Strings.Changed += () => PropertyChanged?.Invoke(this, new(nameof(Value)));
    }

    public string Value => (string)_property.GetValue(null)!;

    public event PropertyChangedEventHandler? PropertyChanged;
}
