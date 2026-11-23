using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.Dap;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>A row of a variables tree: a frame's variable, an object's field, an array's element, a watch or an evaluated
/// expression. Its children are asked for as it is first expanded. <see cref="Owner"/> is what Set Value writes through
/// (a frame, an object or an array); a row without one is set by assigning to its <see cref="Expression"/>.</summary>
public sealed partial class VariableNode : Observable
{
    private readonly Func<int, Task<IReadOnlyList<JvmDebugVariable>>>? load;
    private string value;
    private string type;
    private int reference;
    private bool isExpanded;
    private bool isEditing;
    private bool loaded;

    public VariableNode(string name, string value, string type, int reference, Func<int, Task<IReadOnlyList<JvmDebugVariable>>>? load,
        bool isWatch = false, int owner = 0, string? expression = null)
    {
        Name = name;
        this.value = value;
        this.type = type;
        this.reference = reference;
        IsWatch = isWatch;
        Owner = owner;
        Expression = expression ?? name;
        this.load = load;
        Placeholder();
    }

    public string Name { get; }

    public string Value
    {
        get => value;
        private set => Set(ref this.value, value);
    }

    public string Type
    {
        get => type;
        private set => Set(ref type, value);
    }

    public int Reference => reference;

    public bool IsWatch { get; }

    /// <summary>The frame, object or array whose variable this row is, for Set Value; 0 for a watch or an expression.</summary>
    public int Owner { get; }

    /// <summary>The Java that reads this row: what Add to Watches adds.</summary>
    public string Expression { get; }

    /// <summary>A row that holds a value, as opposed to a message ("Collecting data…", an error).</summary>
    public bool HasValue => Name.Length > 0 && Name != "..." && !Value.StartsWith("Cannot ", StringComparison.Ordinal);

    public ObservableCollection<VariableNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value) && value && !loaded) _ = LoadAsync();
        }
    }

    /// <summary>Set Value is open on the row: its value is a text box.</summary>
    public bool IsEditing
    {
        get => isEditing;
        set => Set(ref isEditing, value);
    }

    /// <summary>The text Set Value starts from: the value as Java would write it, without the object's {Type@id}.</summary>
    public string EditText => Value.StartsWith('{') && Value.IndexOf("} ", StringComparison.Ordinal) is int end and > 0
        ? Value[(end + 2)..]
        : Value.StartsWith('{') ? "" : Value;

    /// <summary>Shows a value read again: a new object's children are asked for again as it opens.</summary>
    public void Show(string newValue, string newType, int newReference)
    {
        Value = newValue;
        if (newType.Length > 0) Type = newType;
        if (newReference == reference && newReference == 0) return;
        reference = newReference;
        loaded = false;
        Children.Clear();
        Placeholder();
        if (isExpanded && reference > 0) _ = LoadAsync();
    }

    public override string ToString() => Name.Length == 0 ? Value : $"{Name} = {Value}";

    private void Placeholder()
    {
        if (reference > 0 && load is not null) Children.Add(new VariableNode("", "Collecting data…", "", 0, null));
    }

    private string Child(string name)
    {
        string owner = SimpleChain().IsMatch(Expression) ? Expression : $"({Expression})";
        return name.StartsWith('[') ? owner + name : $"{owner}.{name}";
    }

    [GeneratedRegex(@"^[\w$.\[\]]+$")]
    private static partial Regex SimpleChain();

    private async Task LoadAsync()
    {
        if (load is null || reference == 0) return;
        loaded = true;
        int from = reference;
        try
        {
            IReadOnlyList<JvmDebugVariable> children = await load(from);
            if (from != reference) return;
            Children.Clear();
            foreach (JvmDebugVariable child in children)
                Children.Add(new VariableNode(child.Name, child.Value, child.Type, child.Reference, load, owner: from, expression: Child(child.Name)));
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Children.Clear();
            Children.Add(new VariableNode("", $"Cannot read: {error.Message}", "", 0, null));
        }
    }
}

/// <summary>A tree of <see cref="VariableNode"/>s as IntelliJ's Variables view shows them: Set Value in place (F2 or a
/// double-click; Enter sets, Escape leaves it), and a row's menu with Set Value, Copy Value, Copy Name, Add to Watches,
/// Evaluate and, on a watch, Remove Watch (Delete). What the rows' actions do is the owner's, by the events.</summary>
public sealed class VariablesView : UserControl
{
    private readonly TreeView tree = new();

    public VariablesView(ObservableCollection<VariableNode> rows)
    {
        Rows = rows;
        CodeFonts.Follow(tree, FontFamilyProperty);
        tree.ItemsSource = rows;
        tree.ItemTemplate = new FuncTreeDataTemplate<VariableNode>((node, _) => Row(node), node => node.Children);
        tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(VariableNode.IsExpanded)) { Mode = BindingMode.TwoWay }) },
        });
        tree.KeyDown += (_, e) =>
        {
            if (Selected is not { } node || node.IsEditing) return;
            if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None) e.Handled = StartEditing(node);
            else if (e.Key == Key.Delete && node.IsWatch)
            {
                RemoveRequested?.Invoke(node);
                e.Handled = true;
            }
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = CopyAsync(node.Value);
                e.Handled = true;
            }
        };
        tree.DoubleTapped += (_, e) =>
        {
            if (Selected is { } node && !node.IsEditing && e.Source is Visual source && source.FindAncestorOfType<TextBox>() is null)
                e.Handled = StartEditing(node);
        };
        tree.ContextRequested += (_, e) =>
        {
            if (Selected is not { } node || !node.HasValue) return;
            ContextMenu menu = new() { ItemsSource = MenuFor(node) };
            menu.Open(tree);
            e.Handled = true;
        };
        Content = tree;
    }

    public ObservableCollection<VariableNode> Rows { get; }

    public VariableNode? Selected
    {
        get => tree.SelectedItem as VariableNode;
        set => tree.SelectedItem = value;
    }

    public TreeView Tree => tree;

    /// <summary>Set Value was confirmed on a row with the text typed.</summary>
    public event Action<VariableNode, string>? SetValueRequested;

    /// <summary>A row's expression is to become a watch.</summary>
    public event Action<string>? WatchRequested;

    /// <summary>A row's expression is to be put in the evaluate box.</summary>
    public event Action<string>? EvaluateRequested;

    /// <summary>A watch is to go.</summary>
    public event Action<VariableNode>? RemoveRequested;

    /// <summary>Opens Set Value on a row that holds a value; false for one that holds none.</summary>
    public bool StartEditing(VariableNode node)
    {
        if (!node.HasValue || SetValueRequested is null) return false;
        node.IsEditing = true;
        return true;
    }

    private List<MenuItem> MenuFor(VariableNode node)
    {
        List<MenuItem> items = [];
        MenuItem Item(string header, string? keys, Action run)
        {
            MenuItem item = new() { Header = header, InputGesture = keys is null ? null : KeyGesture.Parse(keys) };
            item.Click += (_, _) => run();
            items.Add(item);
            return item;
        }
        if (SetValueRequested is not null) Item("Set Value…", "F2", () => StartEditing(node));
        Item("Copy Value", "Ctrl+C", () => _ = CopyAsync(node.Value));
        Item("Copy Name", null, () => _ = CopyAsync(node.Name));
        if (WatchRequested is not null && !node.IsWatch) Item("Add to Watches", null, () => WatchRequested(node.Expression));
        if (EvaluateRequested is not null) Item("Evaluate Expression…", null, () => EvaluateRequested(node.Expression));
        if (RemoveRequested is not null && node.IsWatch) Item("Remove Watch", "Delete", () => RemoveRequested(node));
        return items;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private Control Row(VariableNode node)
    {
        TextBlock name = new() { Text = node.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable(node.IsWatch ? TokenKeys.Editor_Info : TokenKeys.Editor_Text));
        TextBlock value = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        value.Bind(TextBlock.TextProperty, new Binding(nameof(VariableNode.Value)) { Source = node });
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (node.Name.Length > 0)
        {
            PathIcon icon = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(node.IsWatch ? IconKeys.Evaluate : IconKeys.Variable));
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(new TextBlock { Text = "=", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        }
        else value.Opacity = 0.6;
        row.Children.Add(value);

        TextBox editor = new() { MinWidth = 160, Padding = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, [Avalonia.Automation.AutomationProperties.NameProperty] = $"Value of {node.Name}" };
        row.Children.Add(editor);
        bool done = false;
        void Finish(bool commit)
        {
            if (done || !node.IsEditing) return;
            done = true;
            string typed = editor.Text ?? "";
            node.IsEditing = false;
            tree.Focus();
            if (commit && typed.Trim().Length > 0) SetValueRequested?.Invoke(node, typed.Trim());
        }
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Finish(commit: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(commit: false);
            }
        };
        editor.LostFocus += (_, _) => Finish(commit: false);
        node.FollowWhileShown(row, (_, e) =>
        {
            if (e.PropertyName is null or nameof(VariableNode.Type)) ToolTip.SetTip(row, node.Type.Length > 0 ? node.Type : null);
            if (e.PropertyName is not (null or nameof(VariableNode.IsEditing))) return;
            bool opening = node.IsEditing && !editor.IsVisible;
            value.IsVisible = !node.IsEditing;
            editor.IsVisible = node.IsEditing;
            if (!opening) return;
            done = false;
            editor.Text = node.EditText;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                editor.Focus();
                editor.SelectAll();
            });
        });
        return row;
    }
}
