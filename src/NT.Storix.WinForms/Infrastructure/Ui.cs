namespace NT.Storix.WinForms.Infrastructure;

/// <summary>Small helpers to build label/control forms in code.</summary>
internal static class Ui
{
    public static TableLayoutPanel Form()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            Padding = new Padding(10),
            AutoScroll = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    public static T Row<T>(this TableLayoutPanel grid, string? label, T control, int height = 0)
        where T : Control
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.AutoSize));

        if (label is not null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 10, 3) }, 0, row);
            grid.Controls.Add(control, 1, row);
        }
        else
        {
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }

        if (height > 0)
        {
            control.Dock = DockStyle.Fill;
        }
        else if (control.Anchor != AnchorStyles.Left)
        {
            // Controls explicitly anchored left keep their own width (numbers, combos, pickers).
            control.Dock = DockStyle.Top;
        }

        control.Margin = new Padding(3, 3, 3, 3);
        return control;
    }

    /// <summary>Adds a final row that absorbs the remaining space.</summary>
    public static void Fill(this TableLayoutPanel grid)
    {
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    }

    public static FlowLayoutPanel Buttons(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static Button Button(string text, EventHandler onClick, int width = 110)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += onClick;
        return button;
    }

    public static TextBox Multiline(int height = 90) => new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        Height = height,
        WordWrap = false,
    };

    public static NumericUpDown Number(int min, int max, decimal value = 0, int decimals = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        Value = Math.Clamp(value, min, max),
        Width = 100,
        Anchor = AnchorStyles.Left,
    };

    public static List<string> Lines(this TextBox box) =>
        box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public static ComboBox EnumCombo<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Anchor = AnchorStyles.Left };
        combo.Items.AddRange(Enum.GetValues<TEnum>().Cast<object>().ToArray());
        combo.SelectedItem = value;
        return combo;
    }
}
