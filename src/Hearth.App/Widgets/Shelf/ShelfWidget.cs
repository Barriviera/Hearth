using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.Shelf;

/// <summary>
/// A drop target for files. Drop things on it to keep them handy (copied;
/// hold Shift to move), drag them back out to wherever they're going.
/// It holds either Hearth's own stash or a real folder such as Downloads.
/// </summary>
public sealed class ShelfWidget : IWidget, IConfigurableWidget
{
    public string Id => "shelf";
    public string Title => "Shelf";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => wide ? 170 : 210;
    public int Order => 120;

    public bool NeedsSetup => false;

    public bool Configure()
    {
        var window = new ShelfSetupWindow(ShelfFolder.Config);
        if (window.ShowDialog() != true || window.Result is null) return false;
        ShelfFolder.Configure(window.Result);
        return true;
    }

    public FrameworkElement CreateView(WidgetContext context) => new ShelfView(context, this);

    private sealed class ShelfView : WidgetView
    {
        private const int Limit = 100;

        private readonly WidgetHeader _header;

        /// <summary>
        /// A grid rather than a wrapping row of fixed-width tiles.
        ///
        /// Wrapping left a gutter the width of most of another tile unused on
        /// every row — the shelf looked half empty at any size that was not an
        /// exact multiple of the tile. The column count is worked out from the
        /// width it is actually given, and the tiles stretch to fill it.
        /// </summary>
        private readonly UniformGrid _tiles = new();

        private int _columns;
        private IReadOnlyList<string> _shown = [];
        private readonly FrameworkElement _empty;
        private readonly Border _dropHint;
        private readonly TextBlock _dropText;

        public ShelfView(WidgetContext context, ShelfWidget widget) : base(context)
        {
            _header = new WidgetHeader(context, "\uE7B8", "Shelf");
            _header.AddAction("\uE838", "Open folder", () => ShellLauncher.Open(ShelfFolder.Location));
            _header.AddAction("\uE713", "Shelf settings", () => widget.Configure());

            _empty = WidgetLayout.Empty(context, "\uE896", "Drop files, pictures or links here");

            _dropText = WidgetChrome.Text(context, "Drop to copy here", 13, WidgetChrome.Accent, FontWeights.SemiBold);
            _dropText.HorizontalAlignment = HorizontalAlignment.Center;
            _dropText.VerticalAlignment = VerticalAlignment.Center;
            _dropHint = new Border
            {
                BorderBrush = WidgetChrome.Accent,
                BorderThickness = new Thickness(1.5 * S),
                CornerRadius = new CornerRadius(12 * S),
                Background = WidgetChrome.Hover,
                Child = _dropText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            var scroller = WidgetLayout.Scroller(_tiles);
            _tiles.SizeChanged += (_, e) =>
            {
                if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 1) return;
                if (Columns(e.NewSize.Width) != _columns) Rebuild();
            };
            layout.Children.Add(new Grid { Children = { scroller, _empty, _dropHint } });
            SetBody(layout);

            AllowDrop = true;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;

            While(() =>
            {
                ShelfFolder.Changed += OnChanged;
                ShelfFolder.Status += OnStatus;
                ShelfFolder.BeginWatching();
            }, () =>
            {
                ShelfFolder.Changed -= OnChanged;
                ShelfFolder.Status -= OnStatus;
                ShelfFolder.EndWatching();
            });
        }

        protected override void OnShown()
        {
            Rebuild();
            ShowStatus(ShelfFolder.CurrentStatus);
        }

        private void OnChanged() => Post(Rebuild);

        private void OnStatus(string? message) => Post(() => ShowStatus(message));

        /// <summary>The drop hint doubles as the "saving" banner.</summary>
        private void ShowStatus(string? message)
        {
            if (message is null)
            {
                _dropHint.Visibility = Visibility.Collapsed;
                return;
            }

            _dropText.Text = message;
            _dropHint.Visibility = Visibility.Visible;
        }

        private void Rebuild()
        {
            var entries = ShelfFolder.Entries(Limit + 1);
            _header.Title = ShelfFolder.Name;
            _header.Glyph = ShelfFolder.IsStash ? "\uE7B8" : "\uE8B7";
            _header.Detail = entries.Count == 0 ? string.Empty : entries.Count > Limit ? $"{Limit}+" : entries.Count.ToString();
            _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var columns = Columns(_tiles.ActualWidth);
            var shown = entries.Take(Limit).ToList();

            // A host that rebuilds its tree hands the same view back unloaded
            // and loaded again, and the bar does that every time any window
            // anywhere is renamed. A tile per file is the whole cost of this
            // widget, so it is only paid when the folder or the shape changed.
            if (columns == _columns && shown.SequenceEqual(_shown, StringComparer.OrdinalIgnoreCase)) return;

            _columns = columns;
            _shown = shown;
            _tiles.Columns = columns;
            _tiles.Children.Clear();
            foreach (var path in shown) _tiles.Children.Add(Tile(path));
        }

        /// <summary>How many tiles of a readable width fit across.</summary>
        private int Columns(double width)
        {
            if (width <= 0) width = ActualWidth > 0 ? ActualWidth : 70 * S;
            return Math.Max(1, (int)Math.Floor(width / (68 * S)));
        }

        private FrameworkElement Tile(string path)
        {
            var item = WidgetFiles.ItemFor(path);
            var icon = WidgetFiles.Icon(Context, item, 40 * S);
            var name = WidgetChrome.Text(Context, item.DisplayName, 11);
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.MaxHeight = name.FontSize * 2.7;
            name.Margin = new Thickness(0, 4 * S, 0, 0);

            var tile = new Pressable(() => ShellLauncher.Open(path))
            {
                // Stretches to its share of the row; the grid decides the width.
                MinWidth = 60 * S,
                CornerRadius = new CornerRadius(10 * S),
                Padding = new Thickness(4 * S, 6 * S, 4 * S, 4 * S),
                Child = new StackPanel { Children = { icon, name } },
                ToolTip = $"{item.DisplayName}\nDrag it out, or click to open",
                DragData = () => WidgetFiles.DragData(path),
            };
            tile.ContextRequested = () => WidgetMenu.Show(tile, menu =>
            {
                WidgetFiles.AddMenuItems(menu, path);
                menu.Items.Add(new Separator());
                menu.Items.Add(WidgetMenu.Item(ShelfFolder.IsStash ? "Remove from shelf" : "Delete",
                    () => ShellLauncher.Recycle(path, WidgetFiles.OwnerOf(this))));
            });
            return tile;
        }

        private static string[]? DroppedPaths(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

        private static bool WantsMove(DragEventArgs e) =>
            (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 && (e.AllowedEffects & DragDropEffects.Move) != 0;

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;

            if (DroppedPaths(e) is { Length: > 0 } paths)
            {
                if (paths.All(IsOnShelf))
                {
                    e.Effects = DragDropEffects.None;
                    _dropHint.Visibility = Visibility.Collapsed;
                    return;
                }

                var move = WantsMove(e);
                e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
                _dropText.Text = move ? $"Drop to move to {ShelfFolder.Name}" : $"Drop to copy to {ShelfFolder.Name}";
                _dropHint.Visibility = Visibility.Visible;
                return;
            }

            // A browser hands over an image as bytes or a URL, never as a path.
            if (BrowserDrop.IsPresent(e.Data))
            {
                e.Effects = Accept(e);
                _dropText.Text = $"Drop to save to {ShelfFolder.Name}";
                _dropHint.Visibility = Visibility.Visible;
                return;
            }

            e.Effects = DragDropEffects.None;
            _dropHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>Browsers offer Copy, Link or both; pick one they allow.</summary>
        private static DragDropEffects Accept(DragEventArgs e) =>
            (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
            : (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
            : DragDropEffects.None;

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            // Leaving a child raises this too; only a real exit hides the hint.
            var p = e.GetPosition(this);
            if (p.X > 0 && p.Y > 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
            ShowStatus(ShelfFolder.CurrentStatus);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _dropHint.Visibility = Visibility.Collapsed;

            if (DroppedPaths(e) is { Length: > 0 } paths)
            {
                var move = WantsMove(e);
                e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
                ShelfFolder.Add(paths, move, WidgetFiles.OwnerOf(this));
                return;
            }

            // The source only keeps its data alive until this returns, so the
            // bytes have to be taken here; writing them happens in the background.
            var content = BrowserDrop.Take(e.Data);
            if (content is null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            e.Effects = Accept(e);
            ShelfFolder.Save(content);
        }

        private static bool IsOnShelf(string path) =>
            string.Equals(Path.GetDirectoryName(path.TrimEnd('\\')), ShelfFolder.Location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
