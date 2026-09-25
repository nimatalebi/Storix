using NT.Storix.Core.Engine;
using NT.Storix.Core.Processing;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Forms;

/// <summary>Browse and search the files inside a backup and pick what to restore.</summary>
internal sealed class BackupBrowserForm : Form
{
    private readonly BackupIndex _index;
    private readonly TreeView _tree = new() { CheckBoxes = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox _search = new() { PlaceholderText = "Search (e.g. report or *.pdf)" };
    private readonly ListView _results = new() { View = View.Details, FullRowSelect = true, CheckBoxes = true, Dock = DockStyle.Fill, Visible = false };
    private readonly Label _summary = new() { AutoSize = true };
    private bool _updating;

    public BackupBrowserForm(BackupIndex index)
    {
        Localizer.Attach(this);
        _index = index;
        Text = $"Files in {index.Archive}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 620);
        MinimizeBox = false;

        _results.Columns.Add("Path", 480);
        _results.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _results.Columns.Add("Modified", 140);

        BuildTree();
        _tree.AfterCheck += (_, e) => OnTreeChecked(e.Node!);
        _results.ItemChecked += (_, e) => OnResultChecked(e.Item);
        _search.TextChanged += (_, _) => ApplySearch();

        var ok = new Button { Text = "Restore selected", DialogResult = DialogResult.OK, Width = 140, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_tree);
        host.Controls.Add(_results);

        var grid = Ui.Form();
        grid.Row(null, new Label { Text = $"{index.Entries.Count:N0} file(s), {BackupJobRunner.FormatSize(index.TotalSize)}. Tick files or folders to restore.", AutoSize = true });
        grid.Row(null, _search);
        grid.Row(null, host, height: 440);
        grid.Row(null, _summary);
        grid.Row(null, Ui.Buttons(ok, cancel));
        grid.Fill();
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        UpdateSummary();
    }

    /// <summary>Selected archive paths (folders end with '/').</summary>
    public IReadOnlyList<string> Selected { get; private set; } = [];

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK && Selected.Count == 0)
        {
            Dialogs.Error(this, "Select at least one file or folder.");
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    private void BuildTree()
    {
        _tree.BeginUpdate();
        foreach (var entry in _index.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var parts = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var nodes = _tree.Nodes;
            var prefix = string.Empty;
            for (var i = 0; i < parts.Length; i++)
            {
                var isFile = i == parts.Length - 1;
                prefix += parts[i] + (isFile ? string.Empty : "/");
                var node = nodes.Cast<TreeNode>().FirstOrDefault(n => (string)n.Tag! == prefix);
                if (node is null)
                {
                    node = new TreeNode(isFile ? $"{parts[i]}  ({BackupJobRunner.FormatSize(entry.Size)})" : parts[i]) { Tag = prefix };
                    nodes.Add(node);
                }

                nodes = node.Nodes;
            }
        }

        if (_tree.Nodes.Count == 1)
        {
            _tree.Nodes[0].Expand();
        }

        _tree.EndUpdate();
    }

    private void OnTreeChecked(TreeNode node)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        SetChildren(node, node.Checked);
        _updating = false;
        CollectFromTree();
    }

    private static void SetChildren(TreeNode node, bool value)
    {
        foreach (TreeNode child in node.Nodes)
        {
            child.Checked = value;
            SetChildren(child, value);
        }
    }

    private void CollectFromTree()
    {
        var selected = new List<string>();
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Checked)
                {
                    selected.Add((string)node.Tag!); // A checked folder covers its children.
                }
                else
                {
                    Walk(node.Nodes);
                }
            }
        }

        Walk(_tree.Nodes);
        Selected = selected;
        UpdateSummary();
    }

    private void ApplySearch()
    {
        var searching = !string.IsNullOrWhiteSpace(_search.Text);
        _tree.Visible = !searching;
        _results.Visible = searching;
        if (!searching)
        {
            CollectFromTree();
            return;
        }

        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var entry in _index.Search(_search.Text).Take(5000))
        {
            _results.Items.Add(new ListViewItem([entry.Path, BackupJobRunner.FormatSize(entry.Size), entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm")]) { Tag = entry.Path });
        }

        _results.EndUpdate();
        Selected = [];
        UpdateSummary();
    }

    private void OnResultChecked(ListViewItem item)
    {
        Selected = _results.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var files = _index.Entries.Where(e => Selected.Any(s => s.EndsWith('/') ? e.Path.StartsWith(s, StringComparison.OrdinalIgnoreCase) : e.Path == s)).ToList();
        _summary.Text = Selected.Count == 0
            ? "Nothing selected."
            : $"Selected: {files.Count:N0} file(s), {BackupJobRunner.FormatSize(files.Sum(f => f.Size))}.";
    }
}
