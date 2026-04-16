using System.Windows;

namespace SnipSimple.Views;

public partial class CountdownWindow : Window
{
    public CountdownWindow(int initialCount)
    {
        InitializeComponent();
        TxtCount.Text = initialCount.ToString();
    }

    public void UpdateCount(int count)
    {
        TxtCount.Text = count > 0 ? count.ToString() : "";
    }
}
