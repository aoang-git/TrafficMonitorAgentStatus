using AgentStatus;
using System.Windows.Forms;

static class SettingsLayoutTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c)));
                using var form = new DetailsForm();
                foreach (var size in new[] { form.Size, form.MinimumSize })
                {
                    form.Size = size; form.PerformLayout();
                    var inputs = Children(form).OfType<NumericUpDown>().ToArray();
                    var grid = (TableLayoutPanel)inputs[0].Parent!; grid.PerformLayout();
                    check(inputs.Length == 4 && inputs.Select(i => grid.GetRow(i)).Distinct().Count() == 2
                        && inputs.Select(i => grid.GetColumn(i)).Distinct().Count() == 2, "four timings stay in two rows and two columns at " + size.Width);
                    check(inputs.All(i => grid.ClientRectangle.Contains(i.Bounds))
                        && inputs.All(i => inputs.All(j => i == j || !i.Bounds.IntersectsWith(j.Bounds))), "timing inputs remain inside layout without overlap at " + size.Width);
                }
                check(Children(form).OfType<Button>().Any(b => b.Text == "发现服务并连接")
                    && !Children(form).Any(c => c.Text is "选择目录" or "Runtime 目录" or "启动显示桥接"), "settings expose one discovery button without directory controls");
                using var display = new DisplaySettingsForm(new SettingsStore()); display.PerformLayout();
                check(!Children(display).OfType<NumericUpDown>().Any() && !Children(display).Any(c => c.Text.Contains("租约")), "display settings no longer expose a lease");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Settings layout verification failed", failure);
    }
}
